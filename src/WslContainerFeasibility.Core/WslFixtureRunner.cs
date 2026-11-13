using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.WSL.Containers;
using static WslContainerFeasibility.ContainerOperations;

namespace WslContainerFeasibility;

/// <summary>Runs a fixed, non-sensitive fixture after verifying container boundaries.</summary>
public sealed class WslFixtureRunner
{
    internal const string ApiPackageVersion = "3.0.5";
    private const string FixtureScript = """
        set -eu
        total="$(awk '{ total += $1 } END { print total }' /workspace/input.txt)"
        [ "$total" = 42 ]
        printf '%s\n' "$total" > /workspace/result.txt
        """;
    private const string FixtureInput = "19\n23\n";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultFixtureTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Checks the installed native WSL container components without installing them.</summary>
    /// <returns>The prerequisite status and a user-readable explanation.</returns>
    public PrerequisiteStatus CheckPrerequisites()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(false, "This feasibility slice requires Windows.");
        }

        try
        {
            var missing = WslcService.GetMissingComponents();
            if (missing.Count != 0)
            {
                return new(false, $"Missing or incompatible WSL components: {string.Join(", ", missing)}.");
            }

            var version = WslcService.GetVersion();
            var installed = new Version((int)version.Major, (int)version.Minor, (int)version.Revision);
            if (installed < new Version(3, 0, 5))
            {
                return new(false, $"WSL container runtime {installed} is too old; version 3.0.5 or later is required.");
            }

            return new(true, $"WSL container runtime {installed} is ready. No components were installed.");
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException
                                          or FileNotFoundException or EntryPointNotFoundException
                                          or BadImageFormatException or TypeInitializationException)
        {
            return new(false, $"WSL container prerequisites could not be checked: {exception.Message}");
        }
    }

    /// <summary>Verifies the boundaries and runs the fixed fixture in a disposable container.</summary>
    /// <param name="progress">Receives non-sensitive progress messages, if supplied.</param>
    /// <param name="cancellationToken">Requests cancellation and container termination.</param>
    /// <returns>The terminal outcome and the path to its local evidence.</returns>
    /// <exception cref="IOException">Evidence could not be persisted.</exception>
    public Task<FixtureRunResult> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(new RunnerTestOptions(), progress, cancellationToken);

    internal Task<FixtureRunResult> RunForTestAsync(
        RunnerTestOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(options, progress, cancellationToken);

    private async Task<FixtureRunResult> RunCoreAsync(
        RunnerTestOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");
        var evidence = new FixtureEvidence
        {
            RunId = runId,
            StartedUtc = DateTimeOffset.UtcNow,
            Windows = Environment.OSVersion.VersionString,
            DotNet = Environment.Version.ToString(),
            WslContainersPackage = ApiPackageVersion,
            ImageReference = ImageReference
        };
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new IOException("The local application-data directory is unavailable.");
        }

        string storagePath = Path.Combine(localAppData, "compareModels", "Feasibility", "runs", runId);
        string canaryDirectory = Path.Combine(Path.GetTempPath(), $"compareModels-canary-{runId}");
        string credentialVariable = $"COMPAREMODELS_CREDENTIAL_{runId.ToUpperInvariant()}";
        bool storageCreated = false;
        bool canaryCreated = false;
        bool variableSet = false;
        bool sessionStartAttempted = false;
        bool sessionTerminated = false;
        Session? session = null;
        Container? taskContainer = null;
        var cleanupErrors = new List<string>();

        try
        {
            progress?.Report("Checking WSL prerequisites.");
            var prerequisites = CheckPrerequisites();
            evidence.Prerequisites = prerequisites.Ready ? "Passed" : "Failed";
            evidence.PrerequisiteDetails = prerequisites.Details;
            if (!prerequisites.Ready)
            {
                throw new BoundaryException(prerequisites.Details);
            }

            cancellationToken.ThrowIfCancellationRequested();
            evidence.WslVersion = await ReadWslVersionAsync(cancellationToken);
            var serviceVersion = WslcService.GetVersion();
            evidence.WslContainerService = $"{serviceVersion.Major}.{serviceVersion.Minor}.{serviceVersion.Revision}";

            if (Directory.Exists(storagePath) || Directory.Exists(canaryDirectory)
                || Environment.GetEnvironmentVariable(credentialVariable) is not null)
            {
                throw new IOException("A generated feasibility resource name already exists.");
            }

            Directory.CreateDirectory(storagePath);
            storageCreated = true;
            Directory.CreateDirectory(canaryDirectory);
            canaryCreated = true;
            string hostCanary = Path.Combine(canaryDirectory, "host-file.txt");
            string credentialCanary = Path.Combine(canaryDirectory, "credential.txt");
            await File.WriteAllTextAsync(hostCanary, "synthetic host-file canary", cancellationToken);
            await File.WriteAllTextAsync(credentialCanary, "synthetic credential canary", cancellationToken);
            Environment.SetEnvironmentVariable(credentialVariable, "synthetic credential canary");
            variableSet = true;

            progress?.Report("Starting a private WSL container session and pulling the pinned image.");
            session = new Session(new SessionSettings($"CompareModels-{runId}", storagePath)
            {
                CpuCount = 1,
                MemorySizeInMB = 512,
                HostLoopback = "host.wslc.internal"
            });
            sessionStartAttempted = true;
            session.Start();
            await PullImageAsync(session, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            taskContainer = CreateContainer(
                session,
                options.UseBridgedTaskNetwork ? ContainerNetworkingMode.Bridged : ContainerNetworkingMode.None,
                ["/bin/sleep", "600"],
                options.MountHostCanaries
                    ? [new ContainerVolume(canaryDirectory, ToWslPath(canaryDirectory), true)]
                    : null);
            taskContainer.Start();
            if (taskContainer.State != ContainerState.Running)
            {
                throw new BoundaryException("The task container did not start.");
            }

            progress?.Report("Verifying host-file, credential, and network boundaries.");
            await VerifyBoundariesAsync(
                session,
                taskContainer,
                hostCanary,
                credentialCanary,
                credentialVariable,
                options,
                evidence,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Running the controlled Linux fixture.");
            evidence.FixtureStarted = true;
            string[] fixtureCommand = options.LongRunningFixture ? CreateLongRunningFixture() : CreateFixture();
            var fixture = await RunProcessAsync(
                taskContainer,
                fixtureCommand,
                options.FixtureTimeout ?? DefaultFixtureTimeout,
                cancellationToken,
                requiredMarker: options.LongRunningFixture ? null : "FIXTURE_DONE",
                outputObserver: options.FixtureOutput);
            evidence.FixtureExitCode = fixture.ExitCode;
            evidence.FixtureOutput = fixture.StandardOutput;
            if (fixture.ExitCode != 0
                || !fixture.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains("RESULT=42", StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The controlled fixture did not produce its expected result. Exit code: {fixture.ExitCode}.");
            }

            evidence.Outcome = FixtureOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            evidence.Outcome = FixtureOutcome.Cancelled;
            evidence.Error = "The run was cancelled.";
        }
        catch (TimeoutException exception) when (evidence.FixtureStarted)
        {
            evidence.Outcome = FixtureOutcome.TimedOut;
            evidence.Error = exception.Message;
        }
        catch (Exception exception)
        {
            evidence.Outcome = evidence.FixtureStarted ? FixtureOutcome.Failed : FixtureOutcome.Blocked;
            evidence.Error = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            try
            {
                progress?.Report("Cleaning up resources owned by this run.");
            }
            catch (Exception exception)
            {
                cleanupErrors.Add($"progress reporting: {exception.Message}");
            }

            if (taskContainer is not null)
            {
                CaptureCleanupError(cleanupErrors, "container", () => StopAndDeleteContainer(taskContainer));
                CaptureCleanupError(cleanupErrors, "container handle", taskContainer.Dispose);
            }

            if (session is not null)
            {
                if (sessionStartAttempted)
                {
                    try
                    {
                        session.Terminate();
                        sessionTerminated = true;
                    }
                    catch (Exception exception)
                    {
                        cleanupErrors.Add($"session: {exception.Message}");
                    }
                }

                CaptureCleanupError(cleanupErrors, "session handle", session.Dispose);
            }

            if (storageCreated && (session is null || sessionTerminated))
            {
                CaptureCleanupError(cleanupErrors, "session storage", () => Directory.Delete(storagePath, recursive: true));
            }
            else if (storageCreated)
            {
                cleanupErrors.Add($"Session storage was retained because termination was not confirmed: {storagePath}");
            }

            if (variableSet)
            {
                CaptureCleanupError(cleanupErrors, "synthetic environment canary",
                    () => Environment.SetEnvironmentVariable(credentialVariable, null));
            }

            if (canaryCreated)
            {
                CaptureCleanupError(cleanupErrors, "synthetic canary directory",
                    () => Directory.Delete(canaryDirectory, recursive: true));
            }
        }

        if (cleanupErrors.Count != 0)
        {
            evidence.Outcome = FixtureOutcome.Failed;
            evidence.CleanupErrors = cleanupErrors;
        }

        evidence.CompletedUtc = DateTimeOffset.UtcNow;
        string evidencePath = await SaveEvidenceAsync(localAppData, evidence.RunId, evidence);
        string summary = evidence.Outcome switch
        {
            FixtureOutcome.Completed => "The controlled fixture completed with result 42.",
            FixtureOutcome.Blocked => $"Execution was blocked: {evidence.Error}",
            FixtureOutcome.Cancelled => "The run was cancelled and its owned resources were cleaned up.",
            FixtureOutcome.TimedOut => "The fixture timed out and its container was stopped.",
            _ => $"The run failed: {evidence.Error ?? "cleanup failed."}"
                + (cleanupErrors.Count == 0 ? string.Empty : $" Cleanup: {string.Join("; ", cleanupErrors)}")
        };
        return new(evidence.Outcome, summary, evidencePath);
    }

    internal static async Task VerifyBoundariesAsync(
        Session session,
        Container container,
        string hostCanary,
        string credentialCanary,
        string credentialVariable,
        RunnerTestOptions options,
        FixtureEvidence evidence,
        CancellationToken cancellationToken)
    {
        using (var inspect = JsonDocument.Parse(container.Inspect()))
        {
            var root = inspect.RootElement;
            var hostConfig = root.GetProperty("HostConfig");
            string? mode = hostConfig.GetProperty("NetworkMode").GetString();
            int mounts = root.GetProperty("Mounts").GetArrayLength();
            string? configuredImage = root.GetProperty("Config").GetProperty("Image").GetString();
            evidence.ImageId = root.GetProperty("Image").GetString();
            evidence.ContainerNetworkMode = mode;
            if (mode != "none" || mounts != 0 || configuredImage != ImageReference
                || string.IsNullOrWhiteSpace(evidence.ImageId))
            {
                evidence.ContainerConfiguration = "Failed";
                throw new BoundaryException(
                    "The task container did not have the required image, no-network, no-mount configuration.");
            }

            evidence.ContainerConfiguration = "Passed";
        }

        var kernel = await RunProcessAsync(
            container,
            ["/bin/sh", "-c", "set -e; uname -r; printf 'KERNEL_DONE\\n'"],
            ProbeTimeout,
            cancellationToken,
            requiredMarker: "KERNEL_DONE");
        string[] kernelLines = kernel.StandardOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (kernel.ExitCode != 0 || kernelLines.Length < 2 || kernelLines[0] == "KERNEL_DONE")
        {
            throw new BoundaryException("The container kernel version could not be determined.");
        }

        evidence.LinuxKernel = kernelLines[0];

        var environment = new Dictionary<string, string>();
        if (options.InjectCredentialCanary)
        {
            environment[credentialVariable] = "synthetic credential canary";
        }

        if (options.UnavailableCanaryTool)
        {
            environment["PATH"] = "/nonexistent";
        }

        var canaries = await RunProcessAsync(
            container,
            [
                "/bin/sh",
                "-c",
                """
                command -v printenv >/dev/null || exit 44
                if [ -r "$1" ]; then exit 41; fi
                if [ -r "$2" ]; then exit 42; fi
                if printenv "$3" >/dev/null; then exit 43; fi
                printf 'CANARIES_BLOCKED\n'
                """,
                "sh",
                ToWslPath(hostCanary),
                ToWslPath(credentialCanary),
                credentialVariable
            ],
            ProbeTimeout,
            cancellationToken,
            requiredMarker: "CANARIES_BLOCKED",
            environment: environment);

        evidence.HostFileCanary = canaries.ExitCode switch
        {
            0 or 42 or 43 => "Passed",
            41 => "Failed",
            _ => "NotRun"
        };
        evidence.CredentialFileCanary = canaries.ExitCode switch
        {
            0 or 43 => "Passed",
            42 => "Failed",
            _ => "NotRun"
        };
        evidence.CredentialEnvironmentCanary = canaries.ExitCode switch
        {
            0 => "Passed",
            43 => "Failed",
            _ => "NotRun"
        };
        if (canaries.ExitCode != 0)
        {
            throw new BoundaryException($"A host or credential canary check failed (exit {canaries.ExitCode}).");
        }

        using var endpoint = new LoopbackEndpoint();
        string hostCanaryLinux = ToWslPath(hostCanary);
        string canaryParent = Path.GetDirectoryName(hostCanary)
            ?? throw new InvalidOperationException("The canary directory is unavailable.");
        string canaryMount = hostCanaryLinux[..hostCanaryLinux.LastIndexOf('/')];
        using var control = CreateContainer(
            session,
            ContainerNetworkingMode.Bridged,
            ["/bin/sleep", "60"],
            [new ContainerVolume(canaryParent, canaryMount, true)]);
        try
        {
            control.Start();
            var fileControl = await RunProcessAsync(
                control,
                [
                    "/bin/sh",
                    "-c",
                    """
                    [ "$(cat "$1")" = 'synthetic host-file canary' ] || exit 51
                    [ "$(cat "$2")" = 'synthetic credential canary' ] || exit 52
                    printf 'FILE_CONTROL_DONE\n'
                    """,
                    "sh",
                    hostCanaryLinux,
                    ToWslPath(credentialCanary)
                ],
                ProbeTimeout,
                cancellationToken,
                requiredMarker: "FILE_CONTROL_DONE");
            evidence.HostFileControl = fileControl.ExitCode switch
            {
                0 or 52 => "Passed",
                51 => "Failed",
                _ => "NotRun"
            };
            evidence.CredentialFileControl = fileControl.ExitCode switch
            {
                0 => "Passed",
                52 => "Failed",
                _ => "NotRun"
            };
            if (fileControl.ExitCode != 0)
            {
                throw new BoundaryException("The synthetic file-canary positive control failed.");
            }

            var positiveRequest = endpoint.WaitForRequestAsync(TimeSpan.FromSeconds(10));
            var positive = await RunProcessAsync(
                control,
                [
                    "/bin/sh",
                    "-c",
                    """
                    ip="$(getent hosts host.wslc.internal | awk '$1 ~ /^[0-9.]+$/ { print $1; exit }')"
                    [ -n "$ip" ] || exit 71
                    printf 'PROBE_IP=%s\n' "$ip"
                    /bin/busybox wget -T 3 -q -O /dev/null "http://$ip:$1/probe" || exit 72
                    printf 'PROBE_DONE\n'
                    """,
                    "sh",
                    endpoint.Port.ToString()
                ],
                ProbeTimeout,
                cancellationToken,
                requiredMarker: "PROBE_DONE");
            if (positive.ExitCode != 0 || !await positiveRequest)
            {
                evidence.NetworkControl = "Failed";
                throw new BoundaryException("The bridged control could not reach the synthetic endpoint.");
            }

            string? addressLine = positive.StandardOutput.Split('\n')
                .FirstOrDefault(line => line.StartsWith("PROBE_IP=", StringComparison.Ordinal));
            string hostAddress = addressLine?["PROBE_IP=".Length..].Trim() ?? string.Empty;
            if (!IPAddress.TryParse(hostAddress, out var address)
                || address.AddressFamily != AddressFamily.InterNetwork)
            {
                evidence.NetworkControl = "Failed";
                throw new BoundaryException("The bridged control did not report a valid IPv4 endpoint address.");
            }

            evidence.NetworkControl = "Passed";

            var blockedRequest = endpoint.WaitForRequestAsync(TimeSpan.FromSeconds(2));
            var blocked = await RunProcessAsync(
                options.UseControlForBlockedProbe ? control : container,
                ["/bin/busybox", "wget", "-T", "3", "-q", "-O", "/dev/null", $"http://{hostAddress}:{endpoint.Port}/probe"],
                ProbeTimeout,
                cancellationToken);
            bool endpointReached = await blockedRequest;
            if (blocked.ExitCode == 0 || endpointReached)
            {
                evidence.BlockedNetwork = "Failed";
                throw new BoundaryException("The no-network task container reached the blocked endpoint.");
            }

            if (blocked.ExitCode != 1)
            {
                throw new BoundaryException($"The blocked-network probe exited unexpectedly ({blocked.ExitCode}).");
            }

            evidence.BlockedNetwork = "Passed";
        }
        finally
        {
            StopAndDeleteContainer(control);
        }
    }

    internal static async Task<CommandResult> RunProcessAsync(
        Container container,
        string[] command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? requiredMarker = null,
        IDictionary<string, string>? environment = null,
        Action<string>? outputObserver = null)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var process = container.CreateProcess(new ProcessSettings
        {
            CommandLine = command,
            EnvironmentVariables = environment ?? new Dictionary<string, string>(),
            OutputMode = ProcessOutputMode.Event
        });
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var markerSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var error = new StringBuilder();
        var outputLock = new Lock();
        process.OutputReceived += data =>
        {
            string text = Encoding.UTF8.GetString(data);
            using (outputLock.EnterScope())
            {
                AppendBounded(output, text);
                if (requiredMarker is not null && output.ToString().Contains(requiredMarker, StringComparison.Ordinal))
                {
                    markerSeen.TrySetResult();
                }
            }

            outputObserver?.Invoke(text);
        };
        process.ErrorReceived += data =>
        {
            using (outputLock.EnterScope())
            {
                AppendBounded(error, Encoding.UTF8.GetString(data));
            }
        };
        process.Exited += code => exited.TrySetResult(code);

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        bool started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
            started = true;
            int exitCode = await exited.Task.WaitAsync(linked.Token);
            if (exitCode == 0 && requiredMarker is not null)
            {
                await markerSeen.Task.WaitAsync(linked.Token);
            }

            using (outputLock.EnterScope())
            {
                return new(exitCode, output.ToString(), error.ToString());
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (container.State == ContainerState.Running)
            {
                container.Stop(Signal.SIGKILL, TimeSpan.FromSeconds(5));
            }

            if (container.State != ContainerState.Exited)
            {
                throw new InvalidOperationException("The container did not stop after cancellation or timeout.");
            }

            if (started)
            {
                try
                {
                    await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException exception)
                {
                    throw new InvalidOperationException("The stopped container process did not report its exit.", exception);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The container process was cancelled.", cancellationToken);
            }

            throw new TimeoutException($"The container process exceeded its {timeout.TotalSeconds:0.#}-second limit.");
        }
    }

    private static string[] CreateFixture() =>
    [
        "/bin/sh",
        "-c",
        """
        set -eu
        mkdir -p /workspace
        printf '%s' "$1" | /bin/busybox base64 -d > /workspace/fixture.sh
        printf '%s' "$2" | /bin/busybox base64 -d > /workspace/input.txt
        /bin/sh /workspace/fixture.sh
        printf 'RESULT='
        cat /workspace/result.txt
        printf 'FIXTURE_DONE\n'
        """,
        "fixture",
        Convert.ToBase64String(Encoding.UTF8.GetBytes(FixtureScript)),
        Convert.ToBase64String(Encoding.UTF8.GetBytes(FixtureInput))
    ];

    private static string[] CreateLongRunningFixture() =>
    [
        "/bin/sh",
        "-c",
        """
        /bin/sh -c 'while :; do printf "HEARTBEAT\n"; /bin/sleep 0.1; done' &
        child=$!
        printf 'CHILD_READY\n'
        wait "$child"
        """
    ];

    internal static async Task PullImageAsync(Session session, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var pull = session.PullImageAsync(new PullImageOptions(ImageReference));
        using var registration = linked.Token.Register(pull.Cancel);
        try
        {
            await pull;
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The image pull was cancelled.", cancellationToken);
            }

            throw new TimeoutException("The pinned image pull exceeded two minutes.");
        }
    }

    internal static async Task<string> ReadWslVersionAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wsl.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Unicode,
            StandardErrorEncoding = Encoding.Unicode,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start wsl.exe to identify the environment.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new TimeoutException("The WSL version check timed out.");
        }

        string version = (await output).Trim();
        string errorText = (await error).Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"The WSL version check failed: {errorText}");
        }

        return version;
    }

    internal static async Task<string> SaveEvidenceAsync(string localAppData, string runId, object evidence)
    {
        string directory = Path.Combine(localAppData, "compareModels", "Feasibility", "Evidence");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{runId}.json");
        string temporaryPath = $"{path}.tmp";
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(evidence, jsonOptions));
            File.Move(temporaryPath, path);
        }
        catch (IOException exception)
        {
            throw new IOException($"Could not persist fixture evidence in {directory}.", exception);
        }

        return path;
    }

    private static void AppendBounded(StringBuilder builder, string value)
    {
        const int limit = 16_384;
        int remaining = limit - builder.Length;
        if (remaining > 0)
        {
            builder.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
        }
    }

    private static void CaptureCleanupError(List<string> errors, string resource, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            errors.Add($"{resource}: {exception.Message}");
        }
    }

    private sealed class BoundaryException(string message) : Exception(message);
}

internal sealed record RunnerTestOptions(
    bool InjectCredentialCanary = false,
    bool UseBridgedTaskNetwork = false,
    bool UseControlForBlockedProbe = false,
    bool UnavailableCanaryTool = false,
    bool MountHostCanaries = false,
    bool LongRunningFixture = false,
    TimeSpan? FixtureTimeout = null,
    Action<string>? FixtureOutput = null);

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class FixtureEvidence
{
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public string Windows { get; init; } = string.Empty;
    public string DotNet { get; init; } = string.Empty;
    public string WslContainersPackage { get; init; } = string.Empty;
    public string ImageReference { get; init; } = string.Empty;
    public string? WslVersion { get; set; }
    public string? WslContainerService { get; set; }
    public string? LinuxKernel { get; set; }
    public string? ImageId { get; set; }
    public string? ContainerNetworkMode { get; set; }
    public string Prerequisites { get; set; } = "NotRun";
    public string? PrerequisiteDetails { get; set; }
    public string ContainerConfiguration { get; set; } = "NotRun";
    public string HostFileCanary { get; set; } = "NotRun";
    public string HostFileControl { get; set; } = "NotRun";
    public string CredentialFileCanary { get; set; } = "NotRun";
    public string CredentialFileControl { get; set; } = "NotRun";
    public string CredentialEnvironmentCanary { get; set; } = "NotRun";
    public string NetworkControl { get; set; } = "NotRun";
    public string BlockedNetwork { get; set; } = "NotRun";
    public bool FixtureStarted { get; set; }
    public int? FixtureExitCode { get; set; }
    public string? FixtureOutput { get; set; }
    public FixtureOutcome Outcome { get; set; } = FixtureOutcome.Blocked;
    public string? Error { get; set; }
    public List<string> CleanupErrors { get; set; } = [];
}
