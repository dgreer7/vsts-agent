using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Expressions = Microsoft.TeamFoundation.DistributedTask.Orchestration.Server.Expressions;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Handlers;
using Microsoft.VisualStudio.Services.Agent.Worker.Container;
using System.Threading;
using System.Linq;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(LinuxContainerOperationProvider))]
    public interface IContainerOperationProvider : IAgentService
    {
        IStep GetContainerStartStep(IExecutionContext jobContext);
        IStep GetContainerStopStep(IExecutionContext jobContext);
        void GetHandlerContainerExecutionCommandline(IExecutionContext executionContext, string filePath, string arguments, string workingDirectory, IDictionary<string, string> environment, out string containerEnginePath, out string containerExecutionArgs);
    }

    public class LinuxContainerOperationProvider : AgentService, IContainerOperationProvider
    {
        private IDockerCommandManager _dockerManger;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _dockerManger = HostContext.GetService<IDockerCommandManager>();
        }

        public IStep GetContainerStartStep(IExecutionContext jobContext)
        {
            return new JobExtensionRunner(context: jobContext.CreateChild(Guid.NewGuid(), StringUtil.Loc("InitializeContainer"), nameof(LinuxContainerOperationProvider)),
                                          runAsync: StartContainerAsync,
                                          condition: ExpressionManager.Succeeded,
                                          displayName: StringUtil.Loc("InitializeContainer"));
        }

        public IStep GetContainerStopStep(IExecutionContext jobContext)
        {
            return new JobExtensionRunner(context: jobContext.CreateChild(Guid.NewGuid(), StringUtil.Loc("StopContainer"), nameof(LinuxContainerOperationProvider)),
                                          runAsync: StopContainerAsync,
                                          condition: ExpressionManager.Always,
                                          displayName: StringUtil.Loc("StopContainer"));
        }

        public void GetHandlerContainerExecutionCommandline(
            IExecutionContext executionContext,
            string filePath,
            string arguments,
            string workingDirectory,
            IDictionary<string, string> environment,
            out string containerEnginePath,
            out string containerExecutionArgs)
        {
            string envOptions = "";
            foreach (var env in environment)
            {
                envOptions += $" -e \"{env.Key}={env.Value.Replace("\"", "\\\"")}\"";
            }

            // we need cd to the workingDir then run the executable with args.
            // bash -c "cd \"workingDirectory\"; \"filePath\" \"arguments\""
            string workingDirectoryEscaped = StringUtil.Format(@"\""{0}\""", workingDirectory.Replace(@"""", @"\\\"""));
            string filePathEscaped = StringUtil.Format(@"\""{0}\""", filePath.Replace(@"""", @"\\\"""));
            string argumentsEscaped = arguments.Replace(@"\", @"\\").Replace(@"""", @"\""");
            string bashCommandLine = $"bash -c \"cd {workingDirectoryEscaped}&{filePathEscaped} {argumentsEscaped}\"";

            arguments = $"exec -u {executionContext.Container.CurrentUserId} {envOptions} {executionContext.Container.ContainerId} {bashCommandLine}";

            containerEnginePath = _dockerManger.DockerPath;
            containerExecutionArgs = arguments;
        }

        private async Task StartContainerAsync(IExecutionContext executionContext)
        {
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNullOrEmpty(executionContext.Container.ContainerImage, nameof(executionContext.Container.ContainerImage));

            // Check docker client/server version
            DockerVersion dockerVersion = await _dockerManger.DockerVersion(executionContext);
            ArgUtil.NotNull(dockerVersion.ServerVersion, nameof(dockerVersion.ServerVersion));
            ArgUtil.NotNull(dockerVersion.ClientVersion, nameof(dockerVersion.ClientVersion));
            Version requiredDockerVersion = new Version(17, 3);
            if (dockerVersion.ServerVersion < requiredDockerVersion)
            {
                throw new NotSupportedException(StringUtil.Loc("MinRequiredDockerServerVersion", requiredDockerVersion, _dockerManger.DockerPath, dockerVersion.ServerVersion));
            }
            if (dockerVersion.ClientVersion < requiredDockerVersion)
            {
                throw new NotSupportedException(StringUtil.Loc("MinRequiredDockerClientVersion", requiredDockerVersion, _dockerManger.DockerPath, dockerVersion.ClientVersion));
            }

            // Pull down docker image
            int pullExitCode = await _dockerManger.DockerPull(executionContext, executionContext.Container.ContainerImage);
            if (pullExitCode != 0)
            {
                throw new InvalidOperationException($"Docker pull fail with exit code {pullExitCode}");
            }

            // Mount folder into container
            executionContext.Container.MountVolumes.Add(new MountVolume(Path.GetDirectoryName(executionContext.Variables.System_DefaultWorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
            executionContext.Container.MountVolumes.Add(new MountVolume(executionContext.Variables.Agent_TempDirectory));
            executionContext.Container.MountVolumes.Add(new MountVolume(executionContext.Variables.Agent_ToolsDirectory));
            executionContext.Container.MountVolumes.Add(new MountVolume(HostContext.GetDirectory(WellKnownDirectory.Externals), true));
            executionContext.Container.MountVolumes.Add(new MountVolume(HostContext.GetDirectory(WellKnownDirectory.Tasks), true));

            // Ensure .taskkey file exist so we can mount it.
            string taskKeyFile = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), ".taskkey");
            if (!File.Exists(taskKeyFile))
            {
                File.WriteAllText(taskKeyFile, string.Empty);
            }
            executionContext.Container.MountVolumes.Add(new MountVolume(taskKeyFile));

            executionContext.Container.ContainerId = await _dockerManger.DockerCreate(executionContext, executionContext.Container.ContainerImage, executionContext.Container.MountVolumes);
            ArgUtil.NotNullOrEmpty(executionContext.Container.ContainerId, nameof(executionContext.Container.ContainerId));

            // Start container
            int startExitCode = await _dockerManger.DockerStart(executionContext, executionContext.Container.ContainerId);
            if (startExitCode != 0)
            {
                throw new InvalidOperationException($"Docker start fail with exit code {startExitCode}");
            }

            // Ensure bash exist in the image
            int execWhichBashExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"which bash");
            if (execWhichBashExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execWhichBashExitCode}");
            }

            // Get current username
            executionContext.Container.CurrentUserName = (await ExecuteCommandAsync(executionContext, "whoami", string.Empty)).FirstOrDefault();
            ArgUtil.NotNullOrEmpty(executionContext.Container.CurrentUserName, nameof(executionContext.Container.CurrentUserName));

            // Get current userId
            executionContext.Container.CurrentUserId = (await ExecuteCommandAsync(executionContext, "id", $"-u {executionContext.Container.CurrentUserName}")).FirstOrDefault();
            ArgUtil.NotNullOrEmpty(executionContext.Container.CurrentUserId, nameof(executionContext.Container.CurrentUserId));

            // Create an user with same uid as the agent run as user inside the container.
            // All command execute in docker will run as Root by default, 
            // this will cause the agent on the host machine doesn't have permission to any new file/folder created inside the container.
            // So, we create a user account with same UID inside the container and let all docker exec command run as that user.
            string containerUserName = string.Empty;

            // Test whether same UID exists inside container
            int execIdExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"id -u {executionContext.Container.CurrentUserId}");
            if (execIdExitCode == 0)
            {
                // User already exist.
                // We need to find the name for the UID
                List<string> userNames = new List<string>();
                int execGrepExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"bash -c \"grep {executionContext.Container.CurrentUserId} /etc/passwd | cut -f1 -d:\"", userNames);
                if (execGrepExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker exec fail with exit code {execGrepExitCode}");
                }

                // Regex match may return multiple username, 
                if (userNames != null && userNames.Count > 0)
                {
                    foreach (string username in userNames)
                    {
                        execIdExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"id -u {username}");
                        if (execIdExitCode == 0)
                        {
                            containerUserName = username;
                            break;
                        }
                    }
                }

                ArgUtil.NotNullOrEmpty(containerUserName, nameof(containerUserName));
            }
            else
            {
                // Create a new user
                containerUserName = $"{executionContext.Container.CurrentUserName}_VSTSContainer";
                int execUseraddExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"useradd -m -u {executionContext.Container.CurrentUserId} {containerUserName}");
                if (execUseraddExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker exec fail with exit code {execUseraddExitCode}");
                }
            }

            // Create a new vsts_sudo group for giving sudo permission
            int execGroupaddExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"groupadd VSTS_Container_SUDO");
            if (execGroupaddExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execGroupaddExitCode}");
            }

            // Add the new created user to the new created VSTS_SUDO group.
            int execUsermodExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"usermod -a -G VSTS_Container_SUDO {containerUserName}");
            if (execUsermodExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execUsermodExitCode}");
            }

            // Allow the new vsts_sudo group run any sudo command without providing password.
            int execEchoExitCode = await _dockerManger.DockerExec(executionContext, executionContext.Container.ContainerId, string.Empty, $"su -c \"echo '%VSTS_Container_SUDO ALL=(ALL:ALL) NOPASSWD:ALL' >> /etc/sudoers\"");
            if (execUsermodExitCode != 0)
            {
                throw new InvalidOperationException($"Docker exec fail with exit code {execEchoExitCode}");
            }
        }

        private async Task StopContainerAsync(IExecutionContext executionContext)
        {
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));

            if (!string.IsNullOrEmpty(executionContext.Container.ContainerId))
            {
                executionContext.Output($"Stop container: {executionContext.Container.ContainerName}");

                int stopExitCode = await _dockerManger.DockerStop(executionContext, executionContext.Container.ContainerId);
                if (stopExitCode != 0)
                {
                    executionContext.Error($"Docker stop fail with exit code {stopExitCode}");
                }
            }
        }

        private async Task<List<string>> ExecuteCommandAsync(IExecutionContext context, string command, string arg)
        {
            context.Command($"{command} {arg}");

            List<string> outputs = new List<string>();
            object outputLock = new object();
            var processInvoker = HostContext.CreateService<IProcessInvoker>();
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        outputs.Add(message.Data);
                    }
                }
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        outputs.Add(message.Data);
                    }
                }
            };

            await processInvoker.ExecuteAsync(
                            workingDirectory: context.Variables.Agent_WorkFolder,
                            fileName: command,
                            arguments: arg,
                            environment: null,
                            requireExitCodeZero: true,
                            outputEncoding: null,
                            cancellationToken: CancellationToken.None);

            foreach (var outputLine in outputs)
            {
                context.Output(outputLine);
            }

            return outputs;
        }
    }
}
