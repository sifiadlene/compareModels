using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.WSL.Containers;

internal static class Program
{
    private const string ImageReference =
        "docker.io/library/alpine@sha256:49340b346798875b5592e5d420241f2f9cfba5f47e62ebb1446a1b34734e898f";
    private const string CredentialCanaryName = "COMPAREMODELS_SYNTHETIC_CREDENTIAL";
    private const string SessionName = "CompareModels-Feasibility";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    private static async Task<int> Main()
    {
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
        var sessionSettings = new SessionSettings(SessionName, storagePath)
        {
            HostLoopback = "host.wslc.internal"
        };

        using var session = new Session(sessionSettings);
        session.Start();

        try
        {
            var pull = session.PullImageAsync(new PullImageOptions(ImageReference));
            pull.Progress = (_, progress) =>
                Console.WriteLine($"Image pull: {progress.Status} {progress.CurrentBytes}/{progress.TotalBytes}");
            await pull;

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
                    $"No-network container reached the local probe endpoint. Output: {blockedOutput}");
            }

            Console.WriteLine("PASS: no-network container could not reach the synthetic local endpoint");
            return 0;
        }
        finally
        {
            session.Terminate();
        }
    }

    private static string[] CreateNetworkProbeCommand(int port) =>
    [
        "/bin/sh",
        "-c",
        """
        if /bin/busybox wget -T 3 -q -O /dev/null "$1"; then
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
        var initProcessSettings = new ProcessSettings
        {
            CommandLine = commandLine,
            OutputMode = ProcessOutputMode.Event
        };
        var containerSettings = new ContainerSettings(ImageReference)
        {
            Name = $"compare-models-feasibility-{Guid.NewGuid():N}",
            InitProcess = initProcessSettings,
            NetworkingMode = networkingMode,
            EnableAutoRemove = false,
            EnableGpu = false,
            Privileged = false,
            Volumes = [],
            NamedVolumes = []
        };

        using var container = session.CreateContainer(containerSettings);
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
            if (container.State == ContainerState.Running)
            {
                container.Stop(Signal.SIGKILL, TimeSpan.FromSeconds(5));
            }

            container.Delete(DeleteContainerOption.Force);
        }
    }

    private static string ToWslPath(string windowsPath)
    {
        string root = Path.GetPathRoot(windowsPath)
            ?? throw new ArgumentException("Expected a rooted Windows path.", nameof(windowsPath));
        if (root.Length != 3 || root[1] != ':')
        {
            throw new ArgumentException("Expected a drive-letter Windows path.", nameof(windowsPath));
        }

        return $"/mnt/{char.ToLowerInvariant(root[0])}/{windowsPath[root.Length..].Replace('\\', '/')}";
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

    private sealed class LoopbackEndpoint : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationToken _stopToken;

        public LoopbackEndpoint()
        {
            _stopToken = _stop.Token;
            _listener.Start();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task<bool> WaitForRequestAsync(TimeSpan timeout)
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellation.Token,
                _stopToken);
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellation.Token);
                NetworkStream stream = client.GetStream();
                byte[] response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
                await stream.WriteAsync(response, cancellation.Token);
                return true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return false;
            }
            catch (SocketException) when (_stopToken.IsCancellationRequested)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
