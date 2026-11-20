using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.WSL.Containers;
using Windows.Storage.Streams;
using static WslContainerFeasibility.ContainerOperations;

namespace WslContainerFeasibility;

/// <summary>Fixed, non-sensitive changes supported by the protected Evaluation spike.</summary>
public enum EvaluationSample
{
    Correct,
    Incorrect,
    Tampered
}

/// <summary>Distinguishes check results from incomplete or blocked Evaluations.</summary>
public enum EvaluationOutcome
{
    Passed,
    Failed,
    Unrunnable,
    Blocked,
    InfrastructureFailure,
    TimedOut,
    Cancelled
}

/// <summary>Reports a protected Evaluation and its retained evidence.</summary>
public sealed record EvaluationRunResult(
    EvaluationOutcome Outcome, string Summary, string EvidencePath, string? ProjectArchivePath);

/// <summary>Evaluates only a fixed synthetic project in a disposable WSL container.</summary>
public sealed class WslProtectedEvaluationRunner
{
    internal const string EvaluationImage =
        "docker.io/library/debian@sha256:734a72b3d6142a71993b4bc580ca01d8ca687f77a2c422d96a961d3376b179bc";
    private const string CheckPath = "/opt/comparemodels-evaluation/check.sh";
    private const string ExpectedOutput = "42\n";
    private const int SessionCpuCount = 1;
    private const int SessionMemorySizeInMb = 512;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultEvaluationLimit = TimeSpan.FromSeconds(15);
    private static readonly string[] DropArguments =
    [
        "/usr/bin/setpriv", "--reuid=65534", "--regid=65534", "--clear-groups",
        "--inh-caps=-all", "--ambient-caps=-all", "--bounding-set=-all", "--no-new-privs"
    ];

    private const string CheckScript = """
        #!/bin/sh
        set -u
        printf 'CHECK_STARTED\n'
        ulimit -f 256 || { printf 'SETUP_FAILED=ulimit\n'; exit 125; }
        [ -r /workspace/sample.sh ] || { printf 'SETUP_FAILED=sample\n'; exit 125; }
        [ -w /run/comparemodels-evaluation ] || { printf 'SETUP_FAILED=output-directory\n'; exit 125; }
        exec 3>/run/comparemodels-evaluation/actual || { printf 'SETUP_FAILED=output-redirection\n'; exit 125; }
        /usr/bin/setpriv --reuid=65534 --regid=65534 --clear-groups \
            --inh-caps=-all --ambient-caps=-all --bounding-set=-all --no-new-privs \
            /bin/sh -c ':' || { printf 'SETUP_FAILED=setpriv\n'; exit 125; }
        printf 'SETUP_READY\n'
        (
            exec /usr/bin/setpriv --reuid=65534 --regid=65534 --clear-groups \
                --inh-caps=-all --ambient-caps=-all --bounding-set=-all --no-new-privs \
                /bin/sh /workspace/sample.sh
        ) > /run/comparemodels-evaluation/actual
        subject_status=$?
        printf 'subject_exit=%s\n' "$subject_status"
        printf 'subject_output:\n'
        /bin/sed 's/^/  /' /run/comparemodels-evaluation/actual
        if [ "$subject_status" -ne 0 ]; then
            printf 'CHECK_FAILED\n'
            exit 1
        fi
        /usr/bin/cmp -s /run/comparemodels-evaluation/expected /run/comparemodels-evaluation/actual
        cmp_status=$?
        if [ "$cmp_status" -eq 0 ]; then
            printf 'CHECK_PASSED\n'
            exit 0
        fi
        if [ "$cmp_status" -eq 1 ]; then
            printf 'CHECK_FAILED\n'
            exit 1
        fi
        printf 'COMPARISON_FAILED=status_%s\n' "$cmp_status"
        exit 125
        """;

    private const string PrivilegeProbe = """
        /usr/bin/id
        /bin/grep -E '^(CapInh|CapEff|CapBnd|CapAmb|NoNewPrivs):' /proc/self/status
        [ "$(/usr/bin/id -u)" = 65534 ] || exit 11
        [ "$(/usr/bin/id -g)" = 65534 ] || exit 12
        [ "$(/usr/bin/id -G)" = 65534 ] || exit 13
        for field in CapInh CapEff CapBnd CapAmb; do
            /bin/grep -q "^${field}:[[:space:]]*0000000000000000$" /proc/self/status || exit 14
        done
        /bin/grep -q '^NoNewPrivs:[[:space:]]*1$' /proc/self/status || exit 15
        printf 'STATE_OK\n'
        """;

    private const string ProtectionProbe = """
        set -eu
        printf 'writable\n' > /workspace/protection-control
        printf 'WORKSPACE_WRITE_OK\n'
        /bin/ln -s /workspace/protection-control /workspace/protection-writable-link
        printf 'via-link\n' > /workspace/protection-writable-link
        [ "$(/bin/cat /workspace/protection-control)" = 'via-link' ] || exit 21
        printf 'WORKSPACE_SYMLINK_WRITE_OK\n'
        /bin/mkdir /workspace/protection-parent
        /bin/mv /workspace/protection-parent /workspace/protection-parent-moved
        /bin/rmdir /workspace/protection-parent-moved
        printf 'WORKSPACE_RENAME_OK\n'
        if (printf 'replaced\n' > /opt/comparemodels-evaluation/check.sh) 2>/dev/null; then
            printf 'DIRECT_WRITE_SUCCEEDED\n'
            exit 31
        fi
        printf 'DIRECT_WRITE_DENIED\n'
        /bin/ln -s /opt/comparemodels-evaluation/check.sh /workspace/protection-link
        if (printf 'replaced\n' > /workspace/protection-link) 2>/dev/null; then
            printf 'SYMLINK_WRITE_SUCCEEDED\n'
            exit 32
        fi
        printf 'SYMLINK_WRITE_DENIED\n'
        if /bin/mv /opt/comparemodels-evaluation /opt/comparemodels-evaluation-moved 2>/dev/null; then
            printf 'CHECK_RENAME_SUCCEEDED\n'
            exit 33
        fi
        printf 'CHECK_RENAME_DENIED\n'
        /bin/rm /workspace/protection-control /workspace/protection-writable-link /workspace/protection-link
        printf 'PROTECTION_OK\n'
        """;

    /// <summary>Runs a single fixed sample, never an arbitrary user project.</summary>
    public Task<EvaluationRunResult> RunAsync(
        EvaluationSample sample,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(sample))
        {
            throw new ArgumentOutOfRangeException(nameof(sample));
        }

        return RunCoreAsync(new EvaluationTestOptions(sample), progress, cancellationToken);
    }

    internal Task<EvaluationRunResult> RunForTestAsync(
        EvaluationTestOptions options, CancellationToken cancellationToken = default) =>
        RunCoreAsync(options, null, cancellationToken);

    private static async Task<EvaluationRunResult> RunCoreAsync(
        EvaluationTestOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        TimeSpan evaluationLimit = options.EvaluationLimit ?? DefaultEvaluationLimit;
        if (evaluationLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The Evaluation limit must be positive.");
        }

        string runId = Guid.NewGuid().ToString("N");
        string sessionName = $"CompareModels-Evaluation-{runId}";
        string containerName = $"compare-models-evaluation-{runId}";
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new IOException("The local application-data directory is unavailable.");
        }

        string artifactDirectory = Path.Combine(localAppData, "compareModels", "Feasibility", "ProtectedEvaluation", runId);
        string storagePath = Path.Combine(localAppData, "compareModels", "Feasibility", "runs", $"evaluation-{runId}");
        string canaryDirectory = Path.Combine(Path.GetTempPath(), $"compareModels-evaluation-canary-{runId}");
        string credentialVariable = $"COMPAREMODELS_EVALUATION_CREDENTIAL_{runId.ToUpperInvariant()}";
        string expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Unix(CheckScript))));
        string expectedOutputHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ExpectedOutput)));
        var evidence = new ProtectedEvaluationEvidence
        {
            RunId = runId,
            StartedUtc = DateTimeOffset.UtcNow,
            Sample = options.Sample,
            SessionCpuCount = SessionCpuCount,
            SessionMemorySizeInMb = SessionMemorySizeInMb,
            EvaluationLimitMilliseconds = evaluationLimit.TotalMilliseconds,
            CheckSha256 = expectedHash,
            ExpectedOutputSha256 = expectedOutputHash,
            BoundaryData = new FixtureEvidence
            {
                RunId = runId,
                StartedUtc = DateTimeOffset.UtcNow,
                Windows = Environment.OSVersion.VersionString,
                DotNet = Environment.Version.ToString(),
                WslContainersPackage = "3.0.5",
                ImageReference = EvaluationImage
            }
        };
        if (Directory.Exists(artifactDirectory))
        {
            throw new IOException("The generated Evaluation artifact directory already exists.");
        }
        Directory.CreateDirectory(artifactDirectory);
        bool storageCreated = false;
        bool canaryCreated = false;
        bool variableSet = false;
        bool sessionStartAttempted = false;
        bool sessionTerminated = false;
        bool workspaceReady = false;
        Session? session = null;
        Container? container = null;
        string? containerId = null;
        string? evidencePath = null;

        try
        {
            progress?.Report("Checking the protected Evaluation prerequisites.");
            var prerequisites = new WslFixtureRunner().CheckPrerequisites();
            evidence.BoundaryData.Prerequisites = prerequisites.Ready ? "Passed" : "Failed";
            evidence.BoundaryData.PrerequisiteDetails = prerequisites.Details;
            if (!prerequisites.Ready)
            {
                throw new EvaluationBoundaryException(prerequisites.Details);
            }

            cancellationToken.ThrowIfCancellationRequested();
            evidence.BoundaryData.WslVersion = await WslFixtureRunner.ReadWslVersionAsync(cancellationToken);
            var service = WslcService.GetVersion();
            var serviceVersion = new Version((int)service.Major, (int)service.Minor, (int)service.Revision);
            evidence.BoundaryData.WslContainerService = serviceVersion.ToString();
            if (serviceVersion != new Version(3, 0, 5))
            {
                throw new EvaluationBoundaryException(
                    "Effective privileged-mode inspection is only verified for WSL container service 3.0.5.");
            }

            if (Directory.Exists(storagePath) || Directory.Exists(canaryDirectory)
                || Environment.GetEnvironmentVariable(credentialVariable) is not null)
            {
                throw new IOException("A generated Evaluation resource name already exists.");
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

            session = new Session(new SessionSettings(sessionName, storagePath)
            {
                CpuCount = SessionCpuCount,
                MemorySizeInMB = SessionMemorySizeInMb,
                HostLoopback = "host.wslc.internal"
            });
            sessionStartAttempted = true;
            session.Start();
            await WslFixtureRunner.PullImageAsync(session, cancellationToken);
            await WslFixtureRunner.PullImageAsync(session, EvaluationImage, cancellationToken);

            container = session.CreateContainer(new ContainerSettings(EvaluationImage)
            {
                Name = containerName,
                InitProcess = new ProcessSettings
                {
                    CommandLine = ["/bin/sleep", "600"],
                    OutputMode = ProcessOutputMode.Event
                },
                NetworkingMode = ContainerNetworkingMode.None,
                EnableAutoRemove = false,
                EnableGpu = false,
                Privileged = false,
                CapabilityDrops = ["ALL"],
                CapabilityAdditions = ["SETUID", "SETGID", "SETPCAP"],
                Volumes = [],
                NamedVolumes = [],
                PortMappings = []
            });
            container.Start();
            if (container.State != ContainerState.Running)
            {
                throw new InvalidOperationException("The Evaluation container did not start.");
            }

            progress?.Report("Verifying the Evaluation boundary and the protected check.");
            using (var inspect = JsonDocument.Parse(container.Inspect()))
            {
                var root = inspect.RootElement;
                var host = root.GetProperty("HostConfig");
                var config = root.GetProperty("Config");
                containerId = root.GetProperty("Id").GetString();
                string? name = root.GetProperty("Name").GetString();
                string[] drops = host.GetProperty("CapDrop").EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty).ToArray();
                string[] additions = host.GetProperty("CapAdd").EnumerateArray()
                    .Select(item => (item.GetString() ?? string.Empty).Replace("CAP_", "", StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal).ToArray();
                string[] command = config.GetProperty("Cmd").EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty).ToArray();
                string[] environment = config.GetProperty("Env").EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty).ToArray();
                bool noEntrypoint = config.TryGetProperty("Entrypoint", out var entrypoint)
                    && (entrypoint.ValueKind == JsonValueKind.Null
                        || (entrypoint.ValueKind == JsonValueKind.Array && entrypoint.GetArrayLength() == 0));
                bool privileged = host.TryGetProperty("Privileged", out var privilegedValue)
                    && privilegedValue.ValueKind != JsonValueKind.False;
                if (containerId is null || containerId.Length != 64
                    || containerId.Any(character => !Uri.IsHexDigit(character))
                    || name != $"/{containerName}" || privileged || !noEntrypoint
                    || !drops.SequenceEqual(["ALL"])
                    || !additions.SequenceEqual(new[] { "SETGID", "SETPCAP", "SETUID" })
                    || !command.SequenceEqual(["/bin/sleep", "600"])
                    || !environment.SequenceEqual(
                        ["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"]))
                {
                    throw new EvaluationBoundaryException("The evaluator identity, capabilities, or init command was not verified.");
                }

                evidence.ContainerId = containerId;
                evidence.ContainerName = name;
                evidence.ContainerInspect = root.Clone();
            }

            var daemonEvidence = new StringBuilder();
            try
            {
                var daemon = await InspectContainerThroughDaemonAsync(sessionName, containerId, daemonEvidence);
                string[] fields = daemon.StandardOutput.Trim().Split('|');
                if (daemon.ExitCode != 0 || fields.Length != 3 || fields[0] != containerId
                    || fields[1] != $"/{containerName}" || fields[2] != "false")
                {
                    throw new EvaluationBoundaryException("The evaluator's effective non-privileged mode was not verified.");
                }
                evidence.EffectivePrivileged = "false";
            }
            catch (Exception exception) when (exception is not EvaluationBoundaryException)
            {
                throw new EvaluationBoundaryException(
                    $"Effective non-privileged mode could not be inspected: {exception.Message}", exception);
            }
            finally
            {
                evidence.DaemonInspection = daemonEvidence.ToString();
            }

            try
            {
                await WslFixtureRunner.VerifyBoundariesAsync(
                    session, container, hostCanary, credentialCanary, credentialVariable,
                    new RunnerTestOptions(), evidence.BoundaryData, cancellationToken, EvaluationImage,
                    (address, port) =>
                    [
                        "/bin/bash", "-c", "exec 3<>/dev/tcp/$1/$2",
                        "network-probe", address, port.ToString()
                    ]);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new EvaluationBoundaryException(
                    $"The evaluator's isolation boundary was not verified: {exception.Message}", exception);
            }
            cancellationToken.ThrowIfCancellationRequested();

            var rawPrivilege = await WslFixtureRunner.RunProcessAsync(
                container,
                ["/bin/sh", "-c", "[ \"$(/usr/bin/id -u)\" = 0 ] || exit 90\n" + Unix(PrivilegeProbe)],
                ProbeTimeout, cancellationToken);
            string[] drop = options.OmitBoundingDrop
                ? DropArguments.Where(argument => argument != "--bounding-set=-all").ToArray()
                : DropArguments;
            var restrictedPrivilege = await WslFixtureRunner.RunProcessAsync(
                container, [.. drop, "/bin/sh", "-c", Unix(PrivilegeProbe)],
                ProbeTimeout, cancellationToken, requiredMarker: "STATE_OK");
            evidence.PrivilegeNegativeControlExitCode = rawPrivilege.ExitCode;
            evidence.PrivilegeNegativeControlOutput = rawPrivilege.StandardOutput + rawPrivilege.StandardError;
            evidence.PrivilegeProbeExitCode = restrictedPrivilege.ExitCode;
            evidence.PrivilegeProbeOutput = restrictedPrivilege.StandardOutput + restrictedPrivilege.StandardError;
            if (rawPrivilege.ExitCode != 11 || restrictedPrivilege.ExitCode != 0)
            {
                throw new EvaluationBoundaryException("The non-root, capability-free workload boundary was not verified.");
            }

            string sample = CreateSample(options, runId);
            var provision = await WslFixtureRunner.RunProcessAsync(
                container,
                [
                    "/bin/sh", "-ec",
                    """
                    mkdir -p /workspace/tests /opt/comparemodels-evaluation /run/comparemodels-evaluation
                    chmod 0777 /workspace /workspace/tests
                    printf '%s' "$1" | /usr/bin/base64 -d > /workspace/sample.sh
                    printf '%s' "$2" | /usr/bin/base64 -d > /workspace/tests/check.sh
                    printf '%s' "$3" | /usr/bin/base64 -d > /opt/comparemodels-evaluation/check.sh
                    printf '%s' "$4" | /usr/bin/base64 -d > /run/comparemodels-evaluation/expected
                    chmod 0666 /workspace/sample.sh /workspace/tests/check.sh
                    chmod 0555 /opt/comparemodels-evaluation
                    chmod "$5" /opt/comparemodels-evaluation/check.sh
                    chmod 0700 /run/comparemodels-evaluation
                    chmod 0444 /run/comparemodels-evaluation/expected
                    printf 'PROVISIONED\n'
                    """,
                    "provision",
                    Base64(sample),
                    Base64("#!/bin/sh\nexit 1\n"),
                    Base64(Unix(CheckScript)),
                    Base64(ExpectedOutput),
                    options.WeakenCheckPermissions ? "0777" : "0555"
                ],
                ProbeTimeout, cancellationToken, requiredMarker: "PROVISIONED");
            if (provision.ExitCode != 0)
            {
                throw new InvalidOperationException($"Could not provision the fixed sample (exit {provision.ExitCode}).");
            }
            workspaceReady = true;
            var protection = await WslFixtureRunner.RunProcessAsync(
                container, [.. DropArguments, "/bin/sh", "-c", Unix(ProtectionProbe)],
                ProbeTimeout, cancellationToken, requiredMarker: "PROTECTION_OK");
            evidence.ProtectionProbeExitCode = protection.ExitCode;
            evidence.ProtectionProbeOutput = protection.StandardOutput + protection.StandardError;
            if (protection.ExitCode != 0)
            {
                throw new EvaluationBoundaryException(
                    $"The protected-check write controls failed or were unverified (exit {protection.ExitCode}).");
            }
            try
            {
                await VerifyCheckAsync(container, expectedHash, expectedOutputHash, cancellationToken);
                evidence.CheckIntegrityBefore = "Passed";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                evidence.CheckIntegrityBefore = "FailedOrUnverified";
                throw;
            }

            string commandPath = options.MissingCheck ? "/opt/comparemodels-evaluation/missing-check" : CheckPath;
            evidence.CheckCommand = commandPath;
            var preflight = await WslFixtureRunner.RunProcessAsync(
                container,
                [
                    "/bin/sh", "-c",
                    """
                    [ -x /bin/sh ] || exit 70
                    if [ ! -f "$1" ] || [ ! -x "$1" ] || [ -L "$1" ]; then exit 11; fi
                    printf 'CHECK_RUNNABLE\n'
                    """,
                    "check-preflight", commandPath
                ],
                ProbeTimeout, cancellationToken, requiredMarker: "CHECK_RUNNABLE");
            evidence.CheckPreflightExitCode = preflight.ExitCode;
            if (preflight.ExitCode == 11)
            {
                evidence.Outcome = EvaluationOutcome.Unrunnable;
                evidence.Error = "The trusted check command could not be started: its executable preflight failed.";
            }
            else if (preflight.ExitCode != 0)
            {
                throw new InvalidOperationException($"The trusted check preflight failed unexpectedly (exit {preflight.ExitCode}).");
            }
            else
            {
                progress?.Report("Running the protected check.");
                var stopwatch = Stopwatch.StartNew();
                var output = new BoundedOutput();
                var error = new BoundedOutput();
                EvaluationExecution execution;
                try
                {
                    execution = await ExecuteAsync(
                        container, [commandPath], evaluationLimit, cancellationToken,
                        output, error,
                        options.Output,
                        options.BeforeStop is null ? null : async () =>
                        {
                            evidence.DescendantBeforeStop = await options.BeforeStop(sessionName, containerId);
                        },
                        options.AfterStop is null ? null : async () =>
                        {
                            evidence.DescendantAfterStop = await options.AfterStop(sessionName, containerId);
                        });
                }
                finally
                {
                    stopwatch.Stop();
                    evidence.EvaluationElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    evidence.CheckStandardOutput = output.Text;
                    evidence.CheckStandardError = error.Text;
                    evidence.OutputTruncated = output.Truncated || error.Truncated;
                    evidence.CheckStarted = evidence.CheckStandardOutput
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Contains("CHECK_STARTED", StringComparer.Ordinal);
                    evidence.CheckSetupReady = evidence.CheckStandardOutput
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Contains("SETUP_READY", StringComparer.Ordinal);
                    evidence.CheckComparisonStatus = evidence.CheckStandardOutput
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault(line => line.StartsWith("COMPARISON_FAILED=", StringComparison.Ordinal));
                }

                evidence.CheckExitCode = execution.ExitCode;
                evidence.Outcome = execution.End switch
                {
                    ExecutionEnd.Cancelled => EvaluationOutcome.Cancelled,
                    ExecutionEnd.TimedOut => EvaluationOutcome.TimedOut,
                    _ when !evidence.CheckStarted || !evidence.CheckSetupReady
                        => EvaluationOutcome.InfrastructureFailure,
                    _ when evidence.CheckComparisonStatus is not null
                        => EvaluationOutcome.InfrastructureFailure,
                    _ when execution.ExitCode == 125
                        => EvaluationOutcome.InfrastructureFailure,
                    _ when execution.ExitCode == 0 => EvaluationOutcome.Passed,
                    _ when execution.ExitCode == 1 => EvaluationOutcome.Failed,
                    _ => EvaluationOutcome.InfrastructureFailure
                };
                if (options.Sample == EvaluationSample.Tampered && execution.End == ExecutionEnd.Completed)
                {
                    string[] requiredControls =
                    [
                        "WORKSPACE_WRITE_OK", "DIRECT_WRITE_DENIED", "SYMLINK_WRITE_DENIED",
                        "CHECK_RENAME_DENIED", "PARENT_FD_WRITE_DENIED"
                    ];
                    string[] errorLines = error.Text.Split(
                        '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (evidence.OutputTruncated
                        || requiredControls.Any(marker => !errorLines.Contains(marker, StringComparer.Ordinal))
                        || errorLines.Any(line => line.EndsWith("_SUCCEEDED", StringComparison.Ordinal)))
                    {
                        evidence.Outcome = EvaluationOutcome.Blocked;
                        evidence.Error = "The synthetic tampering controls failed or could not be verified.";
                    }
                }
                if (evidence.Outcome == EvaluationOutcome.InfrastructureFailure)
                {
                    evidence.Error = $"The trusted check did not report a supported execution (exit {execution.ExitCode}).";
                }

                if (options.StopBeforeIntegrityCheck && execution.End == ExecutionEnd.Completed)
                {
                    container.Stop(Signal.SIGKILL, TimeSpan.FromSeconds(5));
                }
                if (container.State == ContainerState.Running)
                {
                    try
                    {
                        await VerifyCheckAsync(container, expectedHash, expectedOutputHash, CancellationToken.None);
                        evidence.CheckIntegrityAfter = "Passed";
                    }
                    catch
                    {
                        evidence.CheckIntegrityAfter = "FailedOrUnverified";
                        throw;
                    }
                }
                else if (execution.End == ExecutionEnd.Completed)
                {
                    evidence.CheckIntegrityAfter = "FailedOrUnverified";
                    throw new EvaluationBoundaryException(
                        "The evaluator stopped before the protected check could be verified after execution.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            evidence.Outcome = EvaluationOutcome.Cancelled;
            evidence.Error = "The Evaluation was cancelled.";
        }
        catch (EvaluationBoundaryException exception)
        {
            evidence.Outcome = EvaluationOutcome.Blocked;
            evidence.Error = $"{exception.GetType().Name}: {exception.Message}";
        }
        catch (Exception exception)
        {
            evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
            evidence.Error = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            evidence.EvaluationOutcomeBeforeCapture = evidence.Outcome;
            if (container is not null)
            {
                try
                {
                    if (container.State == ContainerState.Running)
                    {
                        container.Stop(Signal.SIGKILL, TimeSpan.FromSeconds(5));
                    }
                    if (workspaceReady && containerId is not null)
                    {
                        if (container.State != ContainerState.Exited)
                        {
                            throw new InvalidOperationException("The evaluator did not stop before artifact capture.");
                        }
                        evidence.ProjectArchivePath = await CaptureProjectAsync(
                            sessionName, containerId, artifactDirectory, options.MissingProjectForCapture);
                    }
                }
                catch (Exception exception)
                {
                    evidence.CaptureError = $"{exception.GetType().Name}: {exception.Message}";
                    evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
                }
            }

            evidence.CleanupState = "Pending";
            try
            {
                evidencePath = await WslFixtureRunner.SaveEvidenceAsync(localAppData, runId, evidence);
            }
            catch (Exception exception)
            {
                evidence.PersistenceErrors.Add($"Before cleanup: {exception.Message}");
                evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
            }

            if (container is not null)
            {
                WslFixtureRunner.CaptureCleanupError(evidence.CleanupErrors, "container", () => StopAndDeleteContainer(container));
                WslFixtureRunner.CaptureCleanupError(evidence.CleanupErrors, "container handle", container.Dispose);
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
                        evidence.CleanupErrors.Add($"session: {exception.Message}");
                    }
                }
                WslFixtureRunner.CaptureCleanupError(evidence.CleanupErrors, "session handle", session.Dispose);
            }
            if (storageCreated && (session is null || sessionTerminated))
            {
                WslFixtureRunner.CaptureCleanupError(
                    evidence.CleanupErrors, "session storage", () => Directory.Delete(storagePath, recursive: true));
            }
            else if (storageCreated)
            {
                evidence.CleanupErrors.Add($"Session storage retained because termination was not confirmed: {storagePath}");
            }
            if (variableSet)
            {
                WslFixtureRunner.CaptureCleanupError(
                    evidence.CleanupErrors, "synthetic environment canary",
                    () => Environment.SetEnvironmentVariable(credentialVariable, null));
            }
            if (canaryCreated)
            {
                WslFixtureRunner.CaptureCleanupError(
                    evidence.CleanupErrors, "synthetic canary directory",
                    () => Directory.Delete(canaryDirectory, recursive: true));
            }
        }

        if (evidence.CleanupErrors.Count != 0)
        {
            evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
        }
        evidence.CleanupState = evidence.CleanupErrors.Count == 0 ? "Complete" : "Failed";
        evidence.CompletedUtc = DateTimeOffset.UtcNow;
        try
        {
            evidencePath = await WslFixtureRunner.SaveEvidenceAsync(
                localAppData, runId, evidence, replaceExisting: evidencePath is not null);
        }
        catch (Exception exception)
        {
            evidence.PersistenceErrors.Add($"Final save: {exception.Message}");
            evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
            string cleanupDetails = evidence.CleanupErrors.Count == 0
                ? "cleanup: complete"
                : $"cleanup: {string.Join("; ", evidence.CleanupErrors)}";
            throw new IOException(
                $"Could not persist final Evaluation evidence; pre-cleanup evidence: {evidencePath ?? "unavailable"}; " +
                $"project artifact directory: {artifactDirectory}; {cleanupDetails}; " +
                $"persistence: {string.Join("; ", evidence.PersistenceErrors)}.",
                exception);
        }

        var details = new List<string>();
        if (evidence.Error is not null) details.Add(evidence.Error);
        if (evidence.CaptureError is not null) details.Add(evidence.CaptureError);
        details.AddRange(evidence.PersistenceErrors);
        details.AddRange(evidence.CleanupErrors);
        string summary = details.Count != 0 ? string.Join("; ", details) : evidence.Outcome switch
        {
            EvaluationOutcome.Passed => "The trusted check passed.",
            EvaluationOutcome.Failed => "The trusted check ran and failed.",
            EvaluationOutcome.Unrunnable => "The trusted check could not start.",
            EvaluationOutcome.Blocked => "A required boundary was not verified.",
            EvaluationOutcome.TimedOut => "The Evaluation exceeded its limit.",
            EvaluationOutcome.Cancelled => "The Evaluation was cancelled.",
            _ => "The Evaluation could not complete."
        };
        return new(evidence.Outcome, summary, evidencePath, evidence.ProjectArchivePath);
    }

    private static async Task VerifyCheckAsync(
        Container container, string expectedHash, string expectedOutputHash, CancellationToken cancellationToken)
    {
        var result = await WslFixtureRunner.RunProcessAsync(
            container,
            [
                "/bin/sh", "-ec",
                """
                [ ! -L /opt/comparemodels-evaluation ]
                [ ! -L /opt/comparemodels-evaluation/check.sh ]
                [ ! -L /run/comparemodels-evaluation ]
                [ ! -L /run/comparemodels-evaluation/expected ]
                [ "$(/usr/bin/stat -c '%u:%a' /)" = '0:755' ]
                [ "$(/usr/bin/stat -c '%u:%a' /opt)" = '0:755' ]
                [ "$(/usr/bin/stat -c '%u:%a' /run)" = '0:755' ]
                [ "$(/usr/bin/stat -c '%u:%a' /opt/comparemodels-evaluation)" = '0:555' ]
                [ "$(/usr/bin/stat -c '%u:%a' /opt/comparemodels-evaluation/check.sh)" = '0:555' ]
                [ "$(/usr/bin/stat -c '%u:%a' /run/comparemodels-evaluation)" = '0:700' ]
                [ "$(/usr/bin/stat -c '%u:%a' /run/comparemodels-evaluation/expected)" = '0:444' ]
                [ "$(/usr/bin/sha256sum /opt/comparemodels-evaluation/check.sh | /usr/bin/cut -d ' ' -f 1)" = "$1" ]
                [ "$(/usr/bin/sha256sum /run/comparemodels-evaluation/expected | /usr/bin/cut -d ' ' -f 1)" = "$2" ]
                printf 'CHECK_INTEGRITY_OK\n'
                """,
                "check-integrity", expectedHash, expectedOutputHash
            ],
            ProbeTimeout, cancellationToken, requiredMarker: "CHECK_INTEGRITY_OK");
        if (result.ExitCode != 0)
        {
            throw new EvaluationBoundaryException(
                $"The protected check identity or permissions failed verification (exit {result.ExitCode}).");
        }
    }

    private static string CreateSample(EvaluationTestOptions options, string runId)
    {
        if (options.LongRunning)
        {
            return Unix($$"""
                #!/bin/sh
                /bin/sh -c 'while :; do printf "HEARTBEAT\n" >&2; /bin/sleep 0.1; done' \
                    comparemodels-evaluation-child-{{runId}} &
                printf 'CHILD_READY\n' >&2
                wait
                """);
        }
        if (options.SubjectExit126)
        {
            return "#!/bin/sh\nexit 126\n";
        }

        return options.Sample switch
        {
            EvaluationSample.Correct => "#!/bin/sh\nprintf '42\\n'\n",
            EvaluationSample.Incorrect => "#!/bin/sh\nprintf '41\\n'\n",
            EvaluationSample.Tampered => Unix("""
                #!/bin/sh
                printf 'writable\n' > /workspace/writable-control || exit 90
                printf 'WORKSPACE_WRITE_OK\n' >&2
                if (printf 'replaced\n' > /opt/comparemodels-evaluation/check.sh) 2>/dev/null; then
                    printf 'DIRECT_WRITE_SUCCEEDED\n' >&2
                else
                    printf 'DIRECT_WRITE_DENIED\n' >&2
                fi
                /bin/ln -s /opt/comparemodels-evaluation/check.sh /workspace/check-link
                if (printf 'replaced\n' > /workspace/check-link) 2>/dev/null; then
                    printf 'SYMLINK_WRITE_SUCCEEDED\n' >&2
                else
                    printf 'SYMLINK_WRITE_DENIED\n' >&2
                fi
                if /bin/mv /opt/comparemodels-evaluation /opt/comparemodels-evaluation-moved 2>/dev/null; then
                    printf 'CHECK_RENAME_SUCCEEDED\n' >&2
                else
                    printf 'CHECK_RENAME_DENIED\n' >&2
                fi
                if (printf 'CHECK_PASSED\n' > "/proc/$PPID/fd/1") 2>/dev/null; then
                    printf 'PARENT_FD_WRITE_SUCCEEDED\n' >&2
                else
                    printf 'PARENT_FD_WRITE_DENIED\n' >&2
                fi
                printf '#!/bin/sh\nexit 0\n' > /workspace/tests/check.sh
                printf 'CHECK_PASSED\n' > /workspace/report.txt
                printf 'CHECK_PASSED\n' >&2
                printf '41\n'
                """),
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
    }

    private static async Task<string> CaptureProjectAsync(
        string sessionName, string containerId, string artifactDirectory, bool missingProject)
    {
        var copy = await RunDaemonCommandAsync(
            sessionName,
            [
                "sh", "-c",
                "set -o pipefail; docker cp \"$1:$2\" - | base64 -w0",
                "copy-project", containerId, missingProject ? "/missing-workspace" : "/workspace"
            ],
            TimeSpan.FromSeconds(20));
        if (copy.ExitCode != 0)
        {
            throw new IOException($"Could not capture the stopped evaluator project: {copy.StandardError}");
        }

        byte[] archive;
        try
        {
            archive = Convert.FromBase64String(copy.StandardOutput);
        }
        catch (FormatException exception)
        {
            throw new IOException("The stopped evaluator project was not a valid encoded archive.", exception);
        }
        if (archive.Length == 0)
        {
            throw new IOException("The stopped evaluator project archive was empty.");
        }
        using (var stream = new MemoryStream(archive))
        using (var reader = new TarReader(stream))
        {
            bool workspaceFound = false;
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                workspaceFound |= entry.Name == "workspace/" && entry.EntryType == TarEntryType.Directory;
            }
            if (!workspaceFound)
            {
                throw new IOException("The stopped evaluator archive did not contain the project directory.");
            }
        }
        string path = Path.Combine(artifactDirectory, "workspace.tar");
        await File.WriteAllBytesAsync(path, archive);
        return path;
    }

    private static async Task<EvaluationExecution> ExecuteAsync(
        Container container, string[] command, TimeSpan limit, CancellationToken cancellationToken,
        BoundedOutput output, BoundedOutput error,
        Action<string>? outputObserver, Func<Task>? beforeStop, Func<Task>? afterStop)
    {
        using var process = container.CreateProcess(new ProcessSettings
        {
            CommandLine = command,
            EnvironmentVariables = new Dictionary<string, string>(),
            OutputMode = ProcessOutputMode.Stream
        });
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += code => exited.TrySetResult(code);
        using var timeout = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        using var stdout = process.GetOutputStream(ProcessOutputHandle.StandardOutput);
        using var stderr = process.GetOutputStream(ProcessOutputHandle.StandardError);
        Task readOutput = ReadStreamAsync(stdout, output, outputObserver);
        Task readError = ReadStreamAsync(stderr, error, outputObserver);
        Task completion = Task.WhenAll(exited.Task, readOutput, readError);
        ExecutionEnd end = ExecutionEnd.Completed;
        try
        {
            await completion.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (beforeStop is not null)
            {
                await beforeStop();
            }
            if (container.State == ContainerState.Running)
            {
                container.Stop(Signal.SIGKILL, TimeSpan.FromSeconds(5));
            }
            if (container.State != ContainerState.Exited)
            {
                throw new InvalidOperationException("The evaluator did not stop after cancellation or timeout.");
            }
            if (afterStop is not null)
            {
                await afterStop();
            }
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            end = cancellationToken.IsCancellationRequested ? ExecutionEnd.Cancelled : ExecutionEnd.TimedOut;
        }
        return new(end, await exited.Task);
    }

    private static async Task ReadStreamAsync(IInputStream stream, BoundedOutput output, Action<string>? observer)
    {
        while (true)
        {
            var buffer = new Windows.Storage.Streams.Buffer(4096);
            IBuffer data = await stream.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.None);
            if (data.Length == 0)
            {
                return;
            }
            using var reader = DataReader.FromBuffer(data);
            byte[] bytes = new byte[data.Length];
            reader.ReadBytes(bytes);
            output.Append(bytes);
            observer?.Invoke(Encoding.UTF8.GetString(bytes));
        }
    }

    private static string Unix(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private sealed class BoundedOutput
    {
        private const int Limit = 1024 * 1024;
        private readonly Lock _gate = new();
        private readonly MemoryStream _bytes = new();
        private bool _truncated;
        public bool Truncated
        {
            get { using (_gate.EnterScope()) return _truncated; }
        }
        public string Text
        {
            get { using (_gate.EnterScope()) return Encoding.UTF8.GetString(_bytes.ToArray()); }
        }

        public void Append(byte[] bytes)
        {
            using (_gate.EnterScope())
            {
                int remaining = Limit - (int)_bytes.Length;
                _bytes.Write(bytes, 0, Math.Min(bytes.Length, remaining));
                _truncated |= bytes.Length > remaining;
            }
        }
    }

    private enum ExecutionEnd { Completed, TimedOut, Cancelled }
    private sealed record EvaluationExecution(ExecutionEnd End, int ExitCode);
    private sealed class EvaluationBoundaryException(string message, Exception? inner = null) : Exception(message, inner);
}

internal sealed record EvaluationTestOptions(
    EvaluationSample Sample,
    bool MissingCheck = false,
    bool OmitBoundingDrop = false,
    bool WeakenCheckPermissions = false,
    bool SubjectExit126 = false,
    bool MissingProjectForCapture = false,
    bool StopBeforeIntegrityCheck = false,
    bool LongRunning = false,
    TimeSpan? EvaluationLimit = null,
    Action<string>? Output = null,
    Func<string, string, Task<string>>? BeforeStop = null,
    Func<string, string, Task<string>>? AfterStop = null);

internal sealed class ProtectedEvaluationEvidence
{
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public EvaluationSample Sample { get; init; }
    public int SessionCpuCount { get; init; }
    public int SessionMemorySizeInMb { get; init; }
    public double EvaluationLimitMilliseconds { get; init; }
    public double? EvaluationElapsedMilliseconds { get; set; }
    [JsonIgnore]
    public FixtureEvidence BoundaryData { get; init; } = new();
    public EvaluationBoundarySnapshot Boundaries => new(
        BoundaryData.Windows, BoundaryData.DotNet, BoundaryData.WslContainersPackage,
        BoundaryData.ImageReference, BoundaryData.WslVersion, BoundaryData.WslContainerService,
        BoundaryData.LinuxKernel, BoundaryData.ImageId, BoundaryData.ContainerNetworkMode,
        BoundaryData.Prerequisites, BoundaryData.PrerequisiteDetails, BoundaryData.ContainerConfiguration,
        BoundaryData.HostFileCanary, BoundaryData.HostFileControl, BoundaryData.CredentialFileCanary,
        BoundaryData.CredentialFileControl, BoundaryData.CredentialEnvironmentCanary,
        BoundaryData.NetworkControl, BoundaryData.NetworkControlExitCode, BoundaryData.NetworkControlOutput,
        BoundaryData.NetworkControlError, BoundaryData.BlockedNetwork);
    public string? ContainerId { get; set; }
    public string? ContainerName { get; set; }
    public JsonElement? ContainerInspect { get; set; }
    public string? DaemonInspection { get; set; }
    public string? EffectivePrivileged { get; set; }
    public int? PrivilegeNegativeControlExitCode { get; set; }
    public string? PrivilegeNegativeControlOutput { get; set; }
    public int? PrivilegeProbeExitCode { get; set; }
    public string? PrivilegeProbeOutput { get; set; }
    public int? ProtectionProbeExitCode { get; set; }
    public string? ProtectionProbeOutput { get; set; }
    public string CheckSha256 { get; init; } = string.Empty;
    public string ExpectedOutputSha256 { get; init; } = string.Empty;
    public string? CheckCommand { get; set; }
    public int? CheckPreflightExitCode { get; set; }
    public string CheckIntegrityBefore { get; set; } = "NotRun";
    public string CheckIntegrityAfter { get; set; } = "NotRun";
    public bool CheckStarted { get; set; }
    public bool CheckSetupReady { get; set; }
    public string? CheckComparisonStatus { get; set; }
    public int? CheckExitCode { get; set; }
    public string? CheckStandardOutput { get; set; }
    public string? CheckStandardError { get; set; }
    public bool OutputTruncated { get; set; }
    public string? DescendantBeforeStop { get; set; }
    public string? DescendantAfterStop { get; set; }
    public EvaluationOutcome EvaluationOutcomeBeforeCapture { get; set; } = EvaluationOutcome.Blocked;
    public EvaluationOutcome Outcome { get; set; } = EvaluationOutcome.Blocked;
    public string? Error { get; set; }
    public string? CaptureError { get; set; }
    public string? ProjectArchivePath { get; set; }
    public string CleanupState { get; set; } = "Pending";
    public List<string> CleanupErrors { get; set; } = [];
    public List<string> PersistenceErrors { get; set; } = [];
}

internal sealed record EvaluationBoundarySnapshot(
    string Windows,
    string DotNet,
    string WslContainersPackage,
    string ImageReference,
    string? WslVersion,
    string? WslContainerService,
    string? LinuxKernel,
    string? ImageId,
    string? ContainerNetworkMode,
    string Prerequisites,
    string? PrerequisiteDetails,
    string ContainerConfiguration,
    string HostFileCanary,
    string HostFileControl,
    string CredentialFileCanary,
    string CredentialFileControl,
    string CredentialEnvironmentCanary,
    string NetworkControl,
    int? NetworkControlExitCode,
    string? NetworkControlOutput,
    string? NetworkControlError,
    string BlockedNetwork);
