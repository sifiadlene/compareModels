using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.WSL.Containers;
using static WslContainerFeasibility.ContainerOperations;

namespace WslContainerFeasibility;

/// <summary>Probes Windows loopback forwarding to app-owned WSL containers.</summary>
public sealed class WslPortForwardingRunner
{
    private const int ContainerPort = 18080;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Runs the forwarding spike and saves its evidence.</summary>
    public async Task<FixtureRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        string runId = Guid.NewGuid().ToString("N");
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new IOException("The local application-data directory is unavailable.");
        }

        string storagePath = Path.Combine(localAppData, "compareModels", "Feasibility", "runs", runId);
        var evidence = new ForwardingEvidence { RunId = runId, Windows = Environment.OSVersion.VersionString };
        var containers = new List<Container>();
        var bridgedOutput = new StringBuilder();
        var bridgedErrorOutput = new StringBuilder();
        var bridgedOutputLock = new Lock();
        Session? session = null;
        bool storageCreated = false;
        bool startAttempted = false;
        bool terminated = false;
        try
        {
            var prerequisites = new WslFixtureRunner().CheckPrerequisites();
            evidence.Prerequisites = prerequisites.Details;
            if (!prerequisites.Ready)
            {
                throw new InvalidOperationException(prerequisites.Details);
            }

            var version = WslcService.GetVersion();
            evidence.WslContainerService = $"{version.Major}.{version.Minor}.{version.Revision}";
            Directory.CreateDirectory(storagePath);
            storageCreated = true;
            session = new Session(new SessionSettings($"CompareModels-{runId}", storagePath)
            {
                CpuCount = 1,
                MemorySizeInMB = 512
            });
            startAttempted = true;
            session.Start();
            await WslFixtureRunner.PullImageAsync(session, cancellationToken);

            int bridgedPort = ReservePort();
            evidence.BridgedWindowsPort = bridgedPort;
            Container? bridged = null;
            try
            {
                bridged = CreateServer(session, ContainerNetworkingMode.Bridged, bridgedPort, $"{runId}-bridged");
                containers.Add(bridged);
                bridged.InitProcess.OutputReceived += data =>
                {
                    using (bridgedOutputLock.EnterScope())
                    {
                        AppendBounded(bridgedOutput, Encoding.UTF8.GetString(data));
                    }
                };
                bridged.InitProcess.ErrorReceived += data =>
                {
                    using (bridgedOutputLock.EnterScope())
                    {
                        AppendBounded(bridgedErrorOutput, Encoding.UTF8.GetString(data));
                    }
                };
                bridged.Start();
                evidence.BridgedInspect = Inspect(bridged);
            }
            catch (Exception exception)
            {
                evidence.BridgedError = $"{exception.GetType().Name}: {exception.Message}";
            }

            // Probe the no-network mapping independently, even if bridged forwarding is unavailable.
            int nonePort;
            do
            {
                nonePort = ReservePort();
            }
            while (nonePort == bridgedPort);
            evidence.NoneWindowsPort = nonePort;
            Container? noneServer = null;
            try
            {
                noneServer = CreateServer(session, ContainerNetworkingMode.None, nonePort, $"{runId}-none");
            }
            catch (ArgumentException exception)
            {
                evidence.NoneMapping = "Unsupported";
                evidence.NoneMappingError =
                    $"WSL Containers does not support port mappings without networking. {exception.Message}";
            }

            if (noneServer is not null)
            {
                containers.Add(noneServer);
                try
                {
                    noneServer.Start();
                    evidence.NoneInspect = Inspect(noneServer);
                    if (evidence.NoneInspect.Value.GetProperty("HostConfig").GetProperty("NetworkMode").GetString() != "none")
                    {
                        throw new InvalidOperationException("The mapped container was not configured with no network.");
                    }
                    var local = await ProbeContainerAsync(noneServer, "127.0.0.1", $"{runId}-none", cancellationToken);
                    if (local.ExitCode != 0)
                    {
                        evidence.NoneMapping = "Unverified: listener did not respond inside the container";
                    }
                    else
                    {
                        evidence.NoneMapping = await WaitForHttpAsync(IPAddress.Loopback, nonePort, $"{runId}-none", cancellationToken)
                            ? "Reachable" : "Unreachable";
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    evidence.NoneMapping = "Unverified";
                    evidence.NoneMappingError = $"{exception.GetType().Name}: {exception.Message}";
                }
            }

            if (bridged is null || evidence.BridgedError is not null)
            {
                throw new InvalidOperationException($"The bridged mapped container could not be started or inspected: {evidence.BridgedError}");
            }
            if (!await WaitForHttpAsync(IPAddress.Loopback, bridgedPort, $"{runId}-bridged", cancellationToken))
            {
                throw new InvalidOperationException("The bridged container listener was not reachable through Windows loopback.");
            }
            evidence.Loopback = "Passed";
            if (!HasLoopbackMapping(evidence.BridgedInspect!.Value, bridgedPort))
            {
                throw new InvalidOperationException("Inspect did not confirm the requested loopback port binding.");
            }
            IPEndPoint[] listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(endpoint => endpoint.Port == bridgedPort).ToArray();
            evidence.WindowsListeners = listeners.Select(endpoint => endpoint.ToString()).ToArray();
            if (listeners.Length == 0 || listeners.Any(endpoint => !endpoint.Address.Equals(IPAddress.Loopback)))
            {
                throw new InvalidOperationException("Windows did not report an exclusively IPv4-loopback listener for the mapped port.");
            }

            IPAddress[] addresses = GetNonLoopbackAddresses();
            if (addresses.Length == 0)
            {
                throw new InvalidOperationException("No active non-loopback host addresses were available to test.");
            }

            foreach (IPAddress address in addresses)
            {
                // A separate positive control distinguishes an inaccessible host interface from a blocked mapping.
                using var control = new TcpListener(address, 0);
                control.Start();
                int controlPort = ((IPEndPoint)control.LocalEndpoint).Port;
                if (!await CanConnectAsync(address, controlPort, cancellationToken))
                {
                    evidence.NonLoopback.Add($"{address}: unverified (control unreachable)");
                    throw new InvalidOperationException($"Host interface {address} could not be verified.");
                }

                if (await CanConnectAsync(address, bridgedPort, cancellationToken))
                {
                    evidence.NonLoopback.Add($"{address}: reachable");
                    throw new InvalidOperationException($"The forwarded port was reachable on {address}.");
                }
                evidence.NonLoopback.Add($"{address}: unreachable");
            }
            evidence.NonLoopbackResult = "Passed";
            if (!await WaitForHttpAsync(IPAddress.Loopback, bridgedPort, $"{runId}-bridged", cancellationToken))
            {
                throw new InvalidOperationException("The listener stopped during the non-loopback checks.");
            }

            string? addressText = GetBridgedAddress(evidence.BridgedInspect.Value);
            if (!IPAddress.TryParse(addressText, out var containerAddress)
                || containerAddress.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(containerAddress))
            {
                throw new InvalidOperationException("Inspect did not provide a usable bridged container IPv4 address.");
            }
            evidence.BridgedContainerAddress = addressText;
            Container networkControl = CreateContainer(session, ContainerNetworkingMode.Bridged, ["/bin/sleep", "600"]);
            containers.Add(networkControl);
            networkControl.Start();
            evidence.NetworkControlInspect = Inspect(networkControl);
            var positive = await ProbeContainerAsync(networkControl, addressText, $"{runId}-bridged", cancellationToken);
            if (positive.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The bridged listener's direct positive control failed with exit code {positive.ExitCode}: "
                    + $"{positive.StandardError}{positive.StandardOutput}");
            }

            Container isolated = CreateContainer(session, ContainerNetworkingMode.None, ["/bin/sleep", "600"]);
            containers.Add(isolated);
            isolated.Start();
            evidence.ControlInspect = Inspect(isolated);
            if (evidence.ControlInspect.Value.GetProperty("HostConfig").GetProperty("NetworkMode").GetString() != "none")
            {
                throw new InvalidOperationException("The control container was not configured with no network.");
            }
            var blocked = await ProbeContainerAsync(isolated, addressText, $"{runId}-bridged", cancellationToken);
            evidence.NoNetworkProbeExitCode = blocked.ExitCode;
            var positiveAfter = await ProbeContainerAsync(networkControl, addressText, $"{runId}-bridged", cancellationToken);
            if (blocked.ExitCode != 1 || positiveAfter.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The no-network control was reachable or could not be verified "
                    + $"(exit code {blocked.ExitCode}: {blocked.StandardOutput}{blocked.StandardError}).");
            }
            evidence.NoNetworkControl = "Passed";
            if (evidence.NoneMapping is not ("Reachable" or "Unreachable" or "Unsupported"))
            {
                throw new InvalidOperationException("Mapping behavior with no networking was not verified.");
            }
            evidence.Outcome = FixtureOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            evidence.Outcome = FixtureOutcome.Cancelled;
            evidence.Error = "The forwarding probe was cancelled.";
        }
        catch (Exception exception)
        {
            evidence.Error = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            foreach (Container container in containers)
            {
                WslFixtureRunner.CaptureCleanupError(evidence.CleanupErrors, "container", () => StopAndDeleteContainer(container));
                WslFixtureRunner.CaptureCleanupError(evidence.CleanupErrors, "container handle", container.Dispose);
            }
            using (bridgedOutputLock.EnterScope())
            {
                evidence.BridgedProcessOutput = bridgedOutput.ToString();
                evidence.BridgedProcessError = bridgedErrorOutput.ToString();
            }
            if (session is not null)
            {
                if (startAttempted)
                {
                    try
                    {
                        session.Terminate();
                        terminated = true;
                    }
                    catch (Exception exception)
                    {
                        evidence.CleanupErrors.Add($"session: {exception.Message}");
                    }
                }
                WslFixtureRunner.CaptureCleanupError(evidence.CleanupErrors, "session handle", session.Dispose);
            }
            if (storageCreated && (session is null || terminated))
            {
                WslFixtureRunner.CaptureCleanupError(evidence.CleanupErrors, "session storage", () => Directory.Delete(storagePath, true));
            }
            else if (storageCreated)
            {
                evidence.CleanupErrors.Add($"Session storage retained because termination was not confirmed: {storagePath}");
            }
        }

        if (evidence.CleanupErrors.Count != 0)
        {
            evidence.Outcome = FixtureOutcome.Failed;
        }
        evidence.CompletedUtc = DateTimeOffset.UtcNow;
        string path = await WslFixtureRunner.SaveEvidenceAsync(localAppData, runId, evidence);
        return new(evidence.Outcome, evidence.Error ?? $"Forwarding probe: {evidence.Outcome}.", path);
    }

    private static Container CreateServer(Session session, ContainerNetworkingMode mode, int windowsPort, string marker)
    {
        string command =
            """
            printf '%s' "$1" > /tmp/marker
            cat > /tmp/serve <<'EOF'
            #!/bin/sh
            cr=$(printf '\r')
            IFS= read -r line || exit 1
            while IFS= read -r line && [ "${line%$cr}" ]; do :; done
            marker=$(cat /tmp/marker)
            printf 'HTTP/1.0 200 OK\r\nContent-Length: %s\r\nConnection: close\r\n\r\n%s' "${#marker}" "$marker"
            EOF
            chmod +x /tmp/serve
            while :; do /bin/busybox nc -l -p 18080 -e /tmp/serve; done
            """;

        return CreateContainer(session, mode,
            ["/bin/sh", "-c", command.ReplaceLineEndings("\n"), "sh", marker],
            portMappings: [new ContainerPortMapping((ushort)windowsPort, ContainerPort, PortProtocol.TCP)
            {
                WindowsAddress = new Windows.Networking.HostName("127.0.0.1")
            }]);
    }

    private static JsonElement Inspect(Container container)
    {
        using var document = JsonDocument.Parse(container.Inspect());
        return document.RootElement.Clone();
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void AppendBounded(StringBuilder buffer, string text)
    {
        const int maxLength = 4096;
        int length = Math.Min(text.Length, maxLength - buffer.Length);
        if (length > 0)
        {
            buffer.Append(text, 0, length);
        }
    }

    internal static IPAddress[] GetNonLoopbackAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address)
            .Where(address => !IPAddress.IsLoopback(address)
                && (address.AddressFamily == AddressFamily.InterNetwork || address.AddressFamily == AddressFamily.InterNetworkV6))
            .Distinct()
            .ToArray();

    internal static async Task<bool> CanConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.ConnectionRefused
            or SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return false;
        }
    }

    internal static bool HasLoopbackMapping(JsonElement inspect, int windowsPort)
    {
        if (!inspect.TryGetProperty("Ports", out var ports)
            || !ports.TryGetProperty($"{ContainerPort}/tcp", out var bindings)
            || bindings.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var entries = bindings.EnumerateArray().ToArray();
        return entries.Length == 1
            && entries[0].TryGetProperty("HostIp", out var ip) && ip.GetString() == "127.0.0.1"
            && entries[0].TryGetProperty("HostPort", out var port) && port.GetString() == windowsPort.ToString();
    }

    internal static string? GetBridgedAddress(JsonElement inspect)
    {
        if (inspect.GetProperty("HostConfig").GetProperty("NetworkMode").GetString() != "bridge"
            || !inspect.GetProperty("NetworkSettings").GetProperty("Networks").TryGetProperty("bridge", out var bridge))
        {
            return null;
        }
        return bridge.GetProperty("IPAddress").GetString();
    }

    internal static async Task<bool> WaitForHttpAsync(IPAddress address, int port, string marker, CancellationToken cancellationToken,
        TimeSpan? probeTimeout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(probeTimeout ?? ProbeTimeout);
        bool connected = false;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                await client.ConnectAsync(address, port, timeout.Token);
                connected = true;
                using NetworkStream stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\n\r\n"), timeout.Token);
                using var reader = new StreamReader(stream);
                string response = await reader.ReadToEndAsync(timeout.Token);
                if (!response.StartsWith("HTTP/1.0 200 ", StringComparison.Ordinal)
                    && !response.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal))
                {
                    string statusLine = response.Split("\r\n", 2, StringSplitOptions.None)[0];
                    throw new InvalidOperationException(
                        string.IsNullOrEmpty(statusLine)
                            ? "The port returned an empty HTTP response."
                            : $"The port returned an unexpected HTTP status line: {statusLine}");
                }
                if (!response.EndsWith($"\r\n\r\n{marker}", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The port responded without the expected listener marker.");
                }
                return true;
            }
            catch (SocketException) when (!connected)
            {
                try
                {
                    await Task.Delay(250, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (connected)
                {
                    throw new InvalidOperationException("The port connected but did not return a verified response.");
                }
                return false;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private static Task<CommandResult> ProbeContainerAsync(Container container, string address, string marker, CancellationToken cancellationToken)
    {
        string command =
            """
            /bin/busybox --list | grep -Fx wget >/dev/null || exit 73
            i=0
            downloaded=0
            while [ "$i" -lt 3 ]; do
                if /bin/busybox wget -T 3 -q -O /tmp/forward-probe "http://$1:18080/"; then
                    downloaded=1
                    break
                fi
                i=$((i + 1))
                /bin/sleep 1
            done
            [ "$downloaded" -eq 1 ] || exit 1
            if grep -Fx "$2" /tmp/forward-probe >/dev/null; then
                exit 0
            fi
            cat /tmp/forward-probe
            exit 74
            """;

        return WslFixtureRunner.RunProcessAsync(container,
            ["/bin/sh", "-c", command.ReplaceLineEndings("\n"), "sh", address, marker],
            ProbeTimeout,
            cancellationToken);
    }
}

internal sealed class ForwardingEvidence
{
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CompletedUtc { get; set; }
    public string Windows { get; init; } = string.Empty;
    public string WslContainersPackage { get; init; } = "3.0.5";
    public string ImageReference { get; init; } = ContainerOperations.ImageReference;
    public string? WslContainerService { get; set; }
    public string? Prerequisites { get; set; }
    public int BridgedWindowsPort { get; set; }
    public int NoneWindowsPort { get; set; }
    public JsonElement? BridgedInspect { get; set; }
    public string? BridgedError { get; set; }
    public JsonElement? NoneInspect { get; set; }
    public JsonElement? ControlInspect { get; set; }
    public JsonElement? NetworkControlInspect { get; set; }
    public string BridgedProcessOutput { get; set; } = string.Empty;
    public string BridgedProcessError { get; set; } = string.Empty;
    public string? BridgedContainerAddress { get; set; }
    public string[] WindowsListeners { get; set; } = [];
    public string Loopback { get; set; } = "NotRun";
    public List<string> NonLoopback { get; set; } = [];
    public string NonLoopbackResult { get; set; } = "NotRun";
    public string NoneMapping { get; set; } = "NotRun";
    public string? NoneMappingError { get; set; }
    public int? NoNetworkProbeExitCode { get; set; }
    public string NoNetworkControl { get; set; } = "NotRun";
    public FixtureOutcome Outcome { get; set; } = FixtureOutcome.Blocked;
    public string? Error { get; set; }
    public List<string> CleanupErrors { get; set; } = [];
}
