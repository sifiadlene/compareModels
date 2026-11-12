using System.Text;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;
using static WslContainerFeasibility.ContainerOperations;

internal static class Program
{
    private const string CredentialCanaryName = "COMPAREMODELS_SYNTHETIC_CREDENTIAL";
    private const string SessionName = "CompareModels-Feasibility";
    private const string ControlSessionName = "CompareModels-Feasibility-Control";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--fixture"])
        {
            var result = await new WslFixtureRunner().RunAsync(
                new Progress<string>(message => Console.WriteLine(message)));
            Console.WriteLine($"{result.Outcome}: {result.Summary}");
            Console.WriteLine($"Evidence: {result.EvidencePath}");
            return result.Outcome == FixtureOutcome.Completed ? 0 : 1;
        }

        if (args.Length != 0)
        {
            throw new ArgumentException("The only supported argument is --fixture.");
        }

        var missingComponents = WslcService.GetMissingComponents();
        if (missingComponents.Count > 0)
        {
            throw new InvalidOperationException(
                $"WSL prerequisites are missing or incompatible: {string.Join(", ", missingComponents)}");
        }

        var version = WslcService.GetVersion();
        Console.WriteLine($"WSL container runtime: {version.Major}.{version.Minor}.{version.Revision}");
        Console.WriteLine($"Windows version: {Environment.OSVersion.Version}");
        Console.WriteLine($".NET version: {Environment.Version}");

        string canaryPath = Path.GetTempFileName();
        string? previousCanaryValue = Environment.GetEnvironmentVariable(CredentialCanaryName);
        Environment.SetEnvironmentVariable(CredentialCanaryName, "synthetic-only");

        try
        {
            await File.WriteAllTextAsync(canaryPath, "synthetic-only");
            return await RunChecksAsync(canaryPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialCanaryName, previousCanaryValue);
            File.Delete(canaryPath);
        }
    }

    private static async Task<int> RunChecksAsync(string canaryPath)
    {
        string storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "compareModels",
            "WslContainerFeasibility");
        string controlStoragePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "compareModels",
            "WslContainerFeasibilityControl");

        using var controlSession = new Session(new SessionSettings(ControlSessionName, controlStoragePath)
        {
            CpuCount = 1,
            MemorySizeInMB = 512
        });
        bool controlSessionStarted = false;
        Container? controlContainer = null;

        try
        {
            controlSession.Start();
            controlSessionStarted = true;
            await PullImageAsync(controlSession);
            controlContainer = CreateContainer(
                controlSession,
                ContainerNetworkingMode.None,
                ["/bin/sleep", "600"]);
            controlContainer.Start();
            if (controlContainer.State != ContainerState.Running)
            {
                throw new InvalidOperationException("The unrelated control container did not start.");
            }

            var sessionSettings = new SessionSettings(SessionName, storagePath)
            {
                HostLoopback = "host.wslc.internal",
                CpuCount = 1,
                MemorySizeInMB = 512
            };

            using var session = new Session(sessionSettings);
            session.Start();
            try
            {
                await PullImageAsync(session);

                var isolationOutput = await RunContainerAsync(
                    session,
                    ContainerNetworkingMode.None,
                    [
                        "/bin/sh",
                        "-c",
                        """
                        status=0
                        if test -r "$1"; then
                            echo HOST_FILE_CANARY_READABLE
                            status=1
                        else
                            echo HOST_FILE_CANARY_BLOCKED
                        fi
                        if [ -n "${COMPAREMODELS_SYNTHETIC_CREDENTIAL:-}" ]; then
                            echo CREDENTIAL_CANARY_INHERITED
                            status=1
                        else
                            echo CREDENTIAL_CANARY_NOT_INHERITED
                        fi
                        exit "$status"
                        """,
                        "sh",
                        ToWslPath(canaryPath)
                    ]);

                RequireMarker(isolationOutput, "HOST_FILE_CANARY_BLOCKED");
                RequireMarker(isolationOutput, "CREDENTIAL_CANARY_NOT_INHERITED");
                RequireAbsentMarker(isolationOutput, "HOST_FILE_CANARY_READABLE");
                RequireAbsentMarker(isolationOutput, "CREDENTIAL_CANARY_INHERITED");
                Console.WriteLine("PASS: synthetic host-file and environment canaries are unavailable to the container");

                using var reachableEndpoint = new LoopbackEndpoint();
                var reachableRequest = reachableEndpoint.WaitForRequestAsync(TimeSpan.FromSeconds(10));
                string reachableOutput = await RunContainerAsync(
                    session,
                    ContainerNetworkingMode.Bridged,
                    CreateNetworkProbeCommand(reachableEndpoint.Port));
                if (!reachableOutput.Contains("NETWORK_ENDPOINT_REACHED", StringComparison.Ordinal)
                    || !await reachableRequest)
                {
                    throw new InvalidOperationException(
                        $"Bridged control container could not reach the local probe endpoint. Output: {reachableOutput}");
                }

                Console.WriteLine("PASS: bridged control container reached the synthetic local endpoint");

                using var blockedEndpoint = new LoopbackEndpoint();
                var blockedRequest = blockedEndpoint.WaitForRequestAsync(TimeSpan.FromSeconds(5));
                string blockedOutput = await RunContainerAsync(
                    session,
                    ContainerNetworkingMode.None,
                    CreateNetworkProbeCommand(blockedEndpoint.Port));
                if (!blockedOutput.Contains("NETWORK_ENDPOINT_BLOCKED", StringComparison.Ordinal)
                    || await blockedRequest)
                {
                    throw new InvalidOperationException(
                        $"No-network container reached the synthetic local endpoint. Output: {blockedOutput}");
                }

                Console.WriteLine("PASS: no-network container could not reach the synthetic local endpoint");
                await VerifyDescendantCleanupAsync(session, simulatedTimeout: false);
                await VerifyDescendantCleanupAsync(session, simulatedTimeout: true);
            }
            finally
            {
                session.Terminate();
            }

            if (controlContainer.State != ContainerState.Running)
            {
                throw new InvalidOperationException(
                    $"Cleaning up the application session changed the unrelated container state to {controlContainer.State}.");
            }

            await VerifyContainerStillUsableAsync(controlContainer);
            Console.WriteLine("PASS: application-session cleanup left the unrelated session and container running");
        }
        finally
        {
            try
            {
                if (controlContainer is not null)
                {
                    StopAndDeleteContainer(controlContainer);
                    controlContainer.Dispose();
                }
            }
            finally
            {
                if (controlSessionStarted)
                {
                    controlSession.Terminate();
                }
            }
        }

        return 0;
    }

    private static async Task PullImageAsync(Session session)
    {
        var pull = session.PullImageAsync(new PullImageOptions(ImageReference));
        pull.Progress = (_, progress) =>
            Console.WriteLine($"Image pull: {progress.Status} {progress.CurrentBytes}/{progress.TotalBytes}");
        await pull;
    }

    private static string[] CreateNetworkProbeCommand(int port) =>
    [
        "/bin/sh",
        "-c",
        """
        if /bin/busybox wget -T 3 -q -O /dev/null "$1" 2>/dev/null; then
            echo NETWORK_ENDPOINT_REACHED
        else
            echo NETWORK_ENDPOINT_BLOCKED
        fi
        """,
        "sh",
        $"http://host.wslc.internal:{port}/probe"
    ];

    private static async Task<string> RunContainerAsync(
        Session session,
        ContainerNetworkingMode networkingMode,
        string[] commandLine)
    {
        using var container = CreateContainer(session, networkingMode, commandLine);
        var output = new StringBuilder();
        var outputLock = new Lock();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        container.InitProcess.OutputReceived += data =>
        {
            using (outputLock.EnterScope())
            {
                output.Append(Encoding.UTF8.GetString(data));
            }
        };
        container.InitProcess.ErrorReceived += data =>
            Console.Error.Write(Encoding.UTF8.GetString(data));
        container.InitProcess.Exited += exitCode => exited.TrySetResult(exitCode);

        try
        {
            container.Start();
            Task completed = await Task.WhenAny(exited.Task, Task.Delay(ProcessTimeout));
            if (completed != exited.Task)
            {
                throw new TimeoutException($"Container process exceeded {ProcessTimeout}.");
            }

            int exitCode = await exited.Task;
            string processOutput;
            using (outputLock.EnterScope())
            {
                processOutput = output.ToString();
            }

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Container process exited with code {exitCode}. Output: {processOutput}");
            }

            return processOutput;
        }
        finally
        {
            StopAndDeleteContainer(container);
        }
    }

    private static async Task VerifyDescendantCleanupAsync(Session session, bool simulatedTimeout)
    {
        string canaryDirectory = Path.Combine(Path.GetTempPath(), $"compareModels-child-{Guid.NewGuid():N}");
        Directory.CreateDirectory(canaryDirectory);
        string heartbeatPath = Path.Combine(canaryDirectory, "heartbeat");
        string token = Guid.NewGuid().ToString("N");
        string scenario = simulatedTimeout ? "timeout" : "cancellation";
        string childCommand =
            "while :; do printf x >> /probe/heartbeat; /bin/sleep 0.1; done";
        string initScript =
            $"/bin/sh -c \"{childCommand}\" descendant-{token} & child=$!; " +
            "echo $child > /probe/child.pid; wait $child";
        using var container = CreateContainer(
            session,
            ContainerNetworkingMode.None,
            ["/bin/sh", "-c", initScript],
            [new ContainerVolume(canaryDirectory, "/probe", false)]);
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var outputLock = new Lock();
        var processExited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        container.InitProcess.OutputReceived += data =>
        {
            using (outputLock.EnterScope())
            {
                standardOutput.Append(Encoding.UTF8.GetString(data));
            }
        };
        container.InitProcess.ErrorReceived += data =>
        {
            using (outputLock.EnterScope())
            {
                standardError.Append(Encoding.UTF8.GetString(data));
            }
        };
        container.InitProcess.Exited += exitCode => processExited.TrySetResult(exitCode);

        try
        {
            container.Start();

            var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while ((!File.Exists(heartbeatPath) || new FileInfo(heartbeatPath).Length < 5)
                   && DateTime.UtcNow < startDeadline
                   && container.State == ContainerState.Running)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            if (!File.Exists(heartbeatPath) || new FileInfo(heartbeatPath).Length < 5)
            {
                string output;
                string error;
                using (outputLock.EnterScope())
                {
                    output = standardOutput.ToString();
                    error = standardError.ToString();
                }

                int? exitCode = processExited.Task.IsCompleted ? await processExited.Task : null;
                throw new InvalidOperationException(
                    $"The {scenario} descendant heartbeat did not start. State={container.State}, " +
                    $"ExitCode={exitCode}, stdout={output}, stderr={error}, inspect={container.Inspect()}");
            }

            string childPidPath = Path.Combine(canaryDirectory, "child.pid");
            if (!File.Exists(childPidPath))
            {
                throw new InvalidOperationException($"The {scenario} descendant did not report its PID.");
            }

            if (simulatedTimeout)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            container.Stop(
                simulatedTimeout ? Signal.SIGKILL : Signal.SIGTERM,
                TimeSpan.FromSeconds(5));

            if (container.State != ContainerState.Exited)
            {
                throw new InvalidOperationException(
                    $"The {scenario} stop left the container in state {container.State}.");
            }

            long sizeAfterStop = new FileInfo(heartbeatPath).Length;
            await Task.Delay(TimeSpan.FromSeconds(1));
            long sizeAfterObservation = new FileInfo(heartbeatPath).Length;
            if (sizeAfterObservation != sizeAfterStop)
            {
                throw new InvalidOperationException(
                    $"The {scenario} stop left a descendant writing to its canary directory.");
            }

            Console.WriteLine($"PASS: {scenario} stopped the container and its canary-writing descendant");
        }
        finally
        {
            try
            {
                StopAndDeleteContainer(container);
            }
            finally
            {
                Directory.Delete(canaryDirectory, recursive: true);
            }
        }
    }

    private static async Task VerifyContainerStillUsableAsync(Container container)
    {
        using var process = container.CreateProcess(new ProcessSettings
        {
            CommandLine = ["/bin/sh", "-c", "exit 0"],
            OutputMode = ProcessOutputMode.Event
        });
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += exitCode => exited.TrySetResult(exitCode);
        process.Start();

        Task completed = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed != exited.Task || await exited.Task != 0)
        {
            throw new InvalidOperationException("The unrelated container did not execute its post-cleanup probe.");
        }
    }

    private static void RequireMarker(string output, string marker)
    {
        if (!output.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected marker '{marker}' was absent. Output: {output}");
        }
    }

    private static void RequireAbsentMarker(string output, string marker)
    {
        if (output.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected marker '{marker}' was present. Output: {output}");
        }
    }

}
