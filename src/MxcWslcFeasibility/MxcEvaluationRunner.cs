using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;
using WslContainerFeasibility;
using static WslContainerFeasibility.ContainerOperations;

namespace MxcWslcFeasibility;

public sealed record MxcEvaluationResult(
    EvaluationOutcome Outcome, string Summary, string EvidencePath, string? ProjectArchivePath);

public sealed class MxcEvaluationRunner
{
    internal static readonly TimeSpan CaptureOperationLimit = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan FreezeOperationLimit = TimeSpan.FromSeconds(1);
    private static readonly string[] DropArguments =
    [
        "/usr/bin/setpriv", "--reuid=65534", "--regid=65534", "--clear-groups",
        "--inh-caps=-all", "--ambient-caps=-all", "--bounding-set=-all", "--no-new-privs"
    ];

    public Task<MxcEvaluationResult> RunAsync(
        EvaluationSample sample, bool acceptRequiredTelemetry, CancellationToken cancellationToken = default) =>
        RunForTestAsync(new(sample), acceptRequiredTelemetry, cancellationToken);

    internal async Task<MxcEvaluationResult> RunForTestAsync(
        MxcEvaluationOptions options, bool acceptRequiredTelemetry, CancellationToken cancellationToken = default,
        string? localApplicationData = null)
    {
        if (!Enum.IsDefined(options.Sample)) throw new ArgumentOutOfRangeException(nameof(options));
        TimeSpan limit = options.Limit ?? TimeSpan.FromSeconds(15);
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        string id = $"CompareModels-MxcEvaluation-{Guid.NewGuid():N}";
        string local = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new IOException("Local application data is unavailable.");
        string directory = Path.Combine(local, "compareModels", "Feasibility", "MxcWslc", id);
        string storage = Path.Combine(directory, "store");
        string canaries = Path.Combine(directory, "canaries");
        string credentialVariable = $"COMPAREMODELS_MXC_CREDENTIAL_{Guid.NewGuid():N}";
        var evidence = new MxcEvaluationEvidence
        {
            RunId = id,
            Sample = options.Sample,
            StartedUtc = DateTimeOffset.UtcNow,
            Windows = Environment.OSVersion.VersionString,
            DotNet = Environment.Version.ToString(),
            RequiredTelemetryAccepted = acceptRequiredTelemetry,
            EvaluationLimitMilliseconds = limit.TotalMilliseconds,
            SessionName = id,
            StoragePath = storage,
            ImageReference = WslProtectedEvaluationRunner.EvaluationImage,
            DescendantMarker = options.LongRunning ? $"comparemodels-evaluation-child-{id}" : null
        };
        MxcProcess? process = null;
        bool spawnAttempted = false;
        bool canaryControlAttempted = false;
        bool variableSet = false;
        string? evidencePath = null;
        string? containerId = null;
        string? projectPath = null;
        var output = new CapturedOutput(options.Output);
        var error = new CapturedOutput(options.Output);
        Task<WaitResult>? wait = null;
        Task? readOutput = null;
        Task? readError = null;
        Task? gateRelease = null;
        Task? kill = null;
        Task? disposal = null;
        var debugOperations = new List<Task>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descendantReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task StartOwnedKill()
        {
            observationStopped.TrySetResult();
            return kill ??= StartKillAsync(process!.Kill, wait);
        }
        try
        {
            if (!acceptRequiredTelemetry)
            {
                throw new MxcBoundaryException("Required MXC ETW diagnostics must be disclosed and accepted before native initialization.");
            }
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            {
                throw new MxcBoundaryException("This packaged MXC spike requires x64 Windows.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            evidence.DaemonPidsBefore = ReadDaemonPids();
            evidence.NativeVersion = MxcPlatform.NativeVersion;
            evidence.ManagedVersion = typeof(MxcContainer).Assembly.GetName().Version?.ToString();
            evidence.TelemetryConsent = MxcTelemetry.GetConsent().ToString();
            evidence.TelemetryPolicy = MxcTelemetry.GetPolicy().ToString();
            evidence.Platform = JsonSerializer.SerializeToElement(MxcPlatform.GetPlatformSupport());
            evidence.Backends = JsonSerializer.SerializeToElement(MxcPlatform.GetAvailableBackends());
            evidence.NativeFiles = ReadNativeIdentities();
            if (evidence.NativeVersion != "1.1.0"
                || !MxcPlatform.GetPlatformSupport().AvailableMethods.Contains(ContainmentBackend.Wslc))
            {
                throw new MxcBoundaryException("The pinned MXC native runtime did not report WSLC support.");
            }
            var prerequisites = new WslFixtureRunner().CheckPrerequisites();
            evidence.Prerequisites = prerequisites.Details;
            if (!prerequisites.Ready) throw new MxcBoundaryException(prerequisites.Details);
            evidence.WslVersion = await WslFixtureRunner.ReadWslVersionAsync(cancellationToken);
            var version = Microsoft.WSL.Containers.WslcService.GetVersion();
            evidence.WslContainerService = $"{version.Major}.{version.Minor}.{version.Revision}";
            if (evidence.WslContainerService != "3.0.5")
            {
                throw new MxcBoundaryException("The debug-only effective boundary inspection is verified only with WSLC 3.0.5.");
            }
            if (!File.Exists(MxcImage.ArchivePath))
            {
                throw new MxcBoundaryException("The pinned image archive is missing. Run prepare-image before Evaluation.");
            }
            evidence.ImageArchiveSha256 = MxcImage.Validate(MxcImage.ArchivePath);
            if (Directory.Exists(directory)) throw new IOException("The generated MXC resource directory already exists.");
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(canaries);
            string sample = options.LongRunning
                ? CreateLongRunningSample(evidence.DescendantMarker!)
                : WslProtectedEvaluationRunner.CreateSample(
                    new(options.Sample, SubjectExit126: options.SubjectExit126), id);
            await File.WriteAllTextAsync(Path.Combine(canaries, "host.txt"), "synthetic host canary", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(canaries, "credential.txt"), "synthetic credential canary", cancellationToken);
            Environment.SetEnvironmentVariable(credentialVariable, "synthetic credential canary");
            variableSet = true;
            canaryControlAttempted = true;
            await VerifyCanaryControlAsync(id, directory, canaries, evidence);
            string check = Unix(WslProtectedEvaluationRunner.CheckScript);
            evidence.CheckSha256 = Hash(check);
            string orchestrator = CreateOrchestrator(options, credentialVariable, ToWslPath(canaries), sample, check);
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(orchestrator));
            var request = CreateMountlessRequest(id, storage,
                $"/bin/sh -c 'exec /bin/sh -c \"$(/usr/bin/printf %s {encoded} | /usr/bin/base64 -d)\"'");
            evidence.Request = JsonSerializer.SerializeToElement(request);
            evidence.WslcRequest = JsonSerializer.SerializeToElement((Containment.Wslc)request.Containment);
            spawnAttempted = true;
            process = MxcContainer.Spawn(request);
            Stream stdin = process.StandardInput ?? throw new IOException("MXC did not expose standard input.");
            Stream stdout = process.StandardOutput ?? throw new IOException("MXC did not expose standard output.");
            Stream stderr = process.StandardError ?? throw new IOException("MXC did not expose standard error.");
            readOutput = ReadAsync(stdout, output, () =>
            {
                if (HasLine(output.Text, "MXC_BOUNDARY_READY")) ready.TrySetResult();
                if (HasLine(output.Text, "MXC_RESULT_READY")) resultReady.TrySetResult();
            });
            readError = ReadAsync(stderr, error, () =>
            {
                if (evidence.DescendantMarker is { } marker && HasCompleteLine(error.Text, $"CHILD_READY={marker}"))
                    descendantReady.TrySetResult();
            });
            wait = process.WaitAsync();
            Task first = await Task.WhenAny(ready.Task, wait).WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
            if (first == ready.Task)
            {
                containerId = await VerifyEffectiveBoundaryAsync(id, evidence);
                evidence.BoundaryVerified = true;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var evaluationWait = await ReleaseAndWaitAsync(async () =>
                    {
                        await stdin.WriteAsync("go\n"u8.ToArray());
                        await stdin.FlushAsync();
                        stdin.Close();
                    }, Task.WhenAny(resultReady.Task, wait),
                        () => ObserveDescendantsAsync(id, containerId),
                        limit, cancellationToken,
                        task => gateRelease = task, debugOperations.Add,
                        StartOwnedKill, evidence,
                        observationReady: options.LongRunning ? descendantReady.Task : null,
                        observationStop: options.LongRunning ? observationStopped.Task : null,
                        onObservationCompleted: snapshot =>
                        {
                            if (options.DescendantObserved is { } onVerified
                                && evidence.DescendantMarker is { } marker
                                && HasSampleDescendant(snapshot, marker))
                                onVerified();
                        });
                    if (!evaluationWait.GateInterrupted)
                    {
                        Task<string> observation = evaluationWait.Observation!;
                        if (evidence.Outcome is not (EvaluationOutcome.Cancelled or EvaluationOutcome.TimedOut))
                        {
                            if (!resultReady.Task.IsCompleted)
                                throw new MxcBoundaryException("The trusted result was not ready before the evaluator exited.");
                            evidence.Outcome = ClassifyReady(output.Text, evidence.BoundaryVerified, output.Truncated || error.Truncated);
                        }
                        evidence.OutcomeBeforeCapture = evidence.Outcome;
                        stopwatch.Stop();
                        byte[]? archive = null;
                        try
                        {
                            archive = await FreezeAndCaptureAsync(
                                () => RunDaemonCheckedAsync(id, ["docker", "pause", containerId], "pause"),
                                async () =>
                                {
                                    var state = await RunDaemonCommandAsync(id, ["docker", "inspect", "--format", "{{.State.Paused}}", containerId], TimeSpan.FromSeconds(15));
                                    return state.ExitCode == 0 && state.StandardOutput.Trim() == "true";
                                },
                                () => CaptureArchiveAsync(id, containerId, options.FailCapture),
                                StartOwnedKill,
                                CaptureOperationLimit, () => evidence.PauseVerified = true,
                                debugOperations.Add);
                        }
                        catch (Exception exception)
                        {
                            evidence.CaptureError = $"{exception.GetType().Name}: {exception.Message}";
                            evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
                        }
                        finally
                        {
                            _ = StartOwnedKill();
                        }
                        if (evidence.OutcomeBeforeCapture is EvaluationOutcome.Cancelled or EvaluationOutcome.TimedOut)
                        {
                            await RecordDescendantObservationAsync(observation, evidence.DescendantMarker, evidence);
                        }
                        if (archive is not null)
                        {
                            string partialPath = Path.Combine(directory, "workspace.tar.partial");
                            try
                            {
                                ValidateArchive(archive);
                                await File.WriteAllBytesAsync(partialPath, archive);
                                projectPath = Path.Combine(directory, "workspace.tar");
                                File.Move(partialPath, projectPath);
                                evidence.ProjectArchivePath = projectPath;
                            }
                            catch (Exception exception)
                            {
                                projectPath = null;
                                evidence.CaptureError = $"{exception.GetType().Name}: {exception.Message}";
                                evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
                                try { if (File.Exists(partialPath)) File.Delete(partialPath); }
                                catch (Exception cleanupException)
                                {
                                    evidence.CleanupErrors.Add($"Partial project archive: {cleanupException.Message}");
                                }
                            }
                        }
                    }
                }
                finally
                {
                    stopwatch.Stop();
                    evidence.EvaluationElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                }
            }
            else
            {
                evidence.BoundaryVerified = false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            evidence.Outcome = EvaluationOutcome.Cancelled;
            evidence.Error = "The MXC Evaluation was cancelled.";
        }
        catch (MxcBoundaryException exception)
        {
            evidence.Outcome = EvaluationOutcome.Blocked;
            evidence.Error = exception.Message;
        }
        catch (MxcException exception)
        {
            evidence.Outcome = exception.Code is ErrorCode.UnsupportedContainment or ErrorCode.BackendUnavailable
                or ErrorCode.PolicyValidation ? EvaluationOutcome.Blocked : EvaluationOutcome.InfrastructureFailure;
            evidence.Error = exception.Message;
            evidence.NativeErrorCode = exception.Code.ToString();
            evidence.NativeOperation = exception.Operation;
            evidence.NativeRemediation = exception.Remediation;
        }
        catch (Exception exception)
        {
            evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
            evidence.Error = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    kill = StartOwnedKill();
                    bool drained = await FinishProcessAsync(kill, wait, readOutput, readError,
                        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60), evidence.CleanupErrors);
                    if (gateRelease is not null)
                    {
                        try { await gateRelease.WaitAsync(TimeSpan.FromSeconds(2)); }
                        catch (TimeoutException) { evidence.CleanupErrors.Add("The stdin gate write is still in progress."); }
                        catch (Exception exception) { evidence.Warnings.Add($"Stdin gate: {exception.Message}"); }
                    }
                    if (drained && kill.IsCompleted)
                    {
                        if (wait is not null) evidence.NativeTimedOut = (await wait).TimedOut;
                        evidence.Warnings.AddRange(process.Warnings);
                    }
                }
                catch (Exception exception)
                {
                    evidence.CleanupErrors.Add($"Process stop: {exception.Message}");
                }
            }
            evidence.StandardOutput = output.Text;
            evidence.StandardError = error.Text;
            evidence.OutputTruncated = output.Truncated || error.Truncated;
            evidence.CheckStarted = HasLine(output.Text, "CHECK_STARTED");
            evidence.CheckExitCode = HasLine(output.Text, "MXC_CHECK_END=0") ? 0
                : HasLine(output.Text, "MXC_CHECK_END=1") ? 1
                : HasLine(output.Text, "MXC_CHECK_UNRUNNABLE") ? 11 : null;
            if (evidence.NativeTimedOut)
            {
                evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
                evidence.Error = "The native MXC deadline elapsed before cleanup.";
            }
            if (evidence.OutputTruncated && evidence.Outcome is
                (EvaluationOutcome.Passed or EvaluationOutcome.Failed or EvaluationOutcome.Unrunnable))
            {
                evidence.Outcome = EvaluationOutcome.Blocked;
                evidence.Error = "The evaluator output exceeded its limit.";
            }
            if (options.Sample == EvaluationSample.Tampered && evidence.Outcome == EvaluationOutcome.Failed
                && new[] { "DIRECT_WRITE_DENIED", "SYMLINK_WRITE_DENIED", "CHECK_RENAME_DENIED", "PARENT_FD_WRITE_DENIED" }
                    .Any(marker => !HasLine(error.Text, marker)))
            {
                evidence.Outcome = EvaluationOutcome.Blocked;
                evidence.Error = "The tampering controls were incomplete or unverified.";
            }
            if (evidence.Outcome == EvaluationOutcome.Blocked && evidence.Error is null)
                evidence.Error = "The trusted boundary or integrity controls failed or were unverified.";
            if (debugOperations.Any(task => !task.IsCompleted))
            {
                try { await Task.WhenAll(debugOperations).WaitAsync(TimeSpan.FromSeconds(6)); }
                catch { }
                if (debugOperations.Any(task => !task.IsCompleted))
                    evidence.CleanupErrors.Add("A same-session debug operation is still in progress.");
            }
            try
            {
                evidencePath = await WslFixtureRunner.SaveEvidenceAsync(local, id, evidence);
            }
            catch (Exception exception)
            {
                evidence.PersistenceErrors.Add($"Before cleanup: {exception.Message}");
                evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
            }
            if (process is not null && (kill is null || kill.IsCompleted)
                && (gateRelease is null || gateRelease.IsCompleted)
                && (wait is null || wait.IsCompleted)
                && (readOutput is null || readOutput.IsCompleted)
                && (readError is null || readError.IsCompleted)
                && debugOperations.All(task => task.IsCompleted))
            {
                try
                {
                    disposal = Task.Run(process.Dispose);
                    _ = disposal.ContinueWith(static task => _ = task.Exception,
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    await disposal.WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch (Exception exception) { evidence.CleanupErrors.Add($"Process disposal: {exception.Message}"); }
            }
            else if (process is not null)
            {
                evidence.CleanupErrors.Add("Process disposal was skipped because an operation is still in progress.");
            }
            if (spawnAttempted)
            {
                try
                {
                    evidence.SessionAbsentAfterCleanup = !await SessionExistsAsync(id);
                    if (!evidence.SessionAbsentAfterCleanup)
                    {
                        throw new IOException("The app-owned MXC session remained active after disposal.");
                    }
                    if (kill is { IsCompleted: false } || gateRelease is { IsCompleted: false }
                        || debugOperations.Any(task => !task.IsCompleted)
                        || (process is not null && disposal is not { IsCompletedSuccessfully: true }))
                        throw new IOException("A process operation is still in progress; the store was retained.");
                    if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);
                }
                catch (Exception exception)
                {
                    evidence.CleanupErrors.Add($"Session/store cleanup: {exception.Message}");
                }
            }
            if (acceptRequiredTelemetry && OperatingSystem.IsWindows())
            {
                evidence.DaemonPidsAfter = ReadDaemonPids();
                evidence.NoNewDaemonObserved = !evidence.DaemonPidsAfter.Except(evidence.DaemonPidsBefore).Any();
                if (!evidence.NoNewDaemonObserved)
                    evidence.CleanupErrors.Add("A new MXC lifecycle daemon was observed; ownership and cleanup are unverified. It was not stopped.");
            }
            if (options.LongRunning && evidence.BoundaryVerified)
            {
                try
                {
                    int count = error.Text.Split("HEARTBEAT\n", StringSplitOptions.None).Length;
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    evidence.HeartbeatStopped = count > 1
                        && error.Text.Split("HEARTBEAT\n", StringSplitOptions.None).Length == count
                        && evidence.SessionAbsentAfterCleanup;
                    if (!evidence.HeartbeatStopped)
                        throw new IOException("Descendant termination was not confirmed by the heartbeat and session checks.");
                }
                catch (Exception exception)
                {
                    evidence.CleanupErrors.Add($"Descendant verification: {exception.Message}");
                }
            }
            if (variableSet) WslFixtureRunner.CaptureCleanupError(evidence.CleanupErrors, "environment canary",
                () => Environment.SetEnvironmentVariable(credentialVariable, null));
            if (Directory.Exists(canaries))
            {
                var ownedOperations = debugOperations
                    .Concat(new Task?[] { kill, gateRelease, wait, readOutput, readError, disposal }.OfType<Task>());
                if (!CanRemoveCanaries(!canaryControlAttempted || evidence.CanaryControlSessionAbsent,
                    !spawnAttempted || evidence.SessionAbsentAfterCleanup,
                    process is null || disposal is { IsCompletedSuccessfully: true }, ownedOperations))
                    evidence.CleanupErrors.Add("Host canaries were retained because owned work or a session is still active.");
                else
                    WslFixtureRunner.CaptureCleanupError(
                        evidence.CleanupErrors, "host canaries", () => Directory.Delete(canaries, recursive: true));
            }
            if (evidence.CleanupErrors.Count > 0) evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
            evidence.CompletedUtc = DateTimeOffset.UtcNow;
            try
            {
                evidencePath = await WslFixtureRunner.SaveEvidenceAsync(local, id, evidence, evidencePath is not null);
            }
            catch (Exception exception)
            {
                throw new IOException($"Final MXC evidence could not be persisted; pre-cleanup evidence: " +
                    $"{evidencePath ?? "unavailable"}; retained project: {projectPath ?? "unavailable"}; " +
                    $"cleanup errors: {string.Join("; ", evidence.CleanupErrors)}.", exception);
            }
        }
        var errors = new List<string>();
        if (evidence.Error is not null) errors.Add(evidence.Error);
        if (evidence.CaptureError is not null) errors.Add(evidence.CaptureError);
        if (evidence.DescendantObservationError is not null) errors.Add(evidence.DescendantObservationError);
        errors.AddRange(evidence.CleanupErrors);
        errors.AddRange(evidence.PersistenceErrors);
        string summary = errors.Count > 0 ? string.Join("; ", errors) : $"MXC WSLC Evaluation: {evidence.Outcome}.";
        return new(evidence.Outcome, summary, evidencePath, projectPath);
    }

    internal static ContainerRequest CreateRequest(
        string name, string workspace, string protectedDirectory, string storage, string command) => new(command)
    {
        Containment = new Containment.Wslc
        {
            Image = MxcImage.Name,
            ImageTarPath = MxcImage.ArchivePath,
            StoragePath = storage,
            CpuCount = 1,
            MemoryMb = 512,
            Gpu = false
        },
        ContainerName = name,
        TimeoutMs = 90_000,
        WorkingDirectory = workspace,
        Environment = new() { ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" },
        InheritDefaultEnvironment = false,
        Filesystem = new()
        {
            ReadwritePaths = { workspace },
            ReadonlyPaths = { protectedDirectory }
        },
        Network = new()
        {
            Egress = new() { Default = NetworkAction.Deny },
            Ingress = new() { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny }
        }
    };

    internal static ContainerRequest CreateMountlessRequest(string name, string storage, string command) => new(command)
    {
        Containment = new Containment.Wslc
        {
            Image = MxcImage.Name,
            ImageTarPath = MxcImage.ArchivePath,
            StoragePath = storage,
            CpuCount = 1,
            MemoryMb = 512,
            Gpu = false
        },
        ContainerName = name,
        TimeoutMs = 90_000,
        Environment = new() { ["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" },
        InheritDefaultEnvironment = false,
        Network = new()
        {
            Egress = new() { Default = NetworkAction.Deny },
            Ingress = new() { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny }
        }
    };

    internal static EvaluationOutcome ClassifyReady(string output, bool boundary, bool truncated)
    {
        if (!boundary || truncated) return EvaluationOutcome.Blocked;
        if (HasLine(output, "MXC_CHECK_UNRUNNABLE")) return EvaluationOutcome.Unrunnable;
        if (!HasLine(output, "MXC_INTEGRITY_AFTER")) return EvaluationOutcome.Blocked;
        if (!HasLine(output, "SETUP_READY")) return EvaluationOutcome.InfrastructureFailure;
        return (HasLine(output, "MXC_CHECK_END=0"), HasLine(output, "MXC_CHECK_END=1")) switch
        {
            (true, false) => EvaluationOutcome.Passed,
            (false, true) => EvaluationOutcome.Failed,
            _ => EvaluationOutcome.InfrastructureFailure
        };
    }

    internal static string CreateLongRunningSample(string marker, string heartbeatPath = "/workspace/heartbeat")
    {
        string child = $"printf x >> {Quote(heartbeatPath)} || exit 1; " +
            "printf 'CHILD_READY=%s\\n' \"$0\" >&2; " +
            $"while :; do printf 'HEARTBEAT\\n' >&2; /bin/sleep 0.1; printf x >> {Quote(heartbeatPath)} || exit 1; done";
        return $$"""
            #!/bin/sh
            /bin/sh -c {{Quote(child)}} {{Quote(marker)}} &
            wait "$!"
            """;
    }

    internal static string CreateOrchestrator(
        MxcEvaluationOptions options, string credentialVariable, string canaries, string sample, string check)
    {
        string drop = string.Join(' ', DropArguments.Where(argument =>
            !options.OmitBoundingDrop || argument != "--bounding-set=-all"));
        string integrity = $$"""
            [ "$(/usr/bin/stat -c '%u:%a' /)" = '0:755' ] &&
            [ "$(/usr/bin/stat -c '%u:%a' /opt)" = '0:755' ] &&
            [ "$(/usr/bin/stat -c '%u:%a' /run)" = '0:755' ] &&
            [ ! -L /opt/comparemodels-evaluation ] &&
            [ ! -L /opt/comparemodels-evaluation/check.sh ] &&
            [ ! -L /run/comparemodels-evaluation ] &&
            [ ! -L /run/comparemodels-evaluation/expected ] &&
            [ "$(/usr/bin/stat -c '%u:%a' /opt/comparemodels-evaluation)" = '0:555' ] &&
            [ "$(/usr/bin/stat -c '%u:%a' /opt/comparemodels-evaluation/check.sh)" = '0:555' ] &&
            [ "$(/usr/bin/stat -c '%u:%a' /run/comparemodels-evaluation)" = '0:700' ] &&
            [ "$(/usr/bin/stat -c '%u:%a' /run/comparemodels-evaluation/expected)" = '0:444' ] &&
            [ "$(/usr/bin/sha256sum /opt/comparemodels-evaluation/check.sh | /usr/bin/cut -d ' ' -f 1)" = '{{Hash(check)}}' ] &&
            [ "$(/usr/bin/sha256sum /run/comparemodels-evaluation/expected | /usr/bin/cut -d ' ' -f 1)" = "$expected_hash" ]
            """;
        return Unix($$"""
            #!/bin/sh
            set -eu
            /usr/bin/id
            /bin/uname -a
            /bin/cat /proc/self/mountinfo
            [ "$(/bin/ls /sys/class/net)" = lo ] || { printf 'EXTERNAL_INTERFACE_PRESENT\n'; exit 70; }
            printf 'NETWORK_NONE_VERIFIED\n'
            [ ! -r {{Quote(canaries + "/host.txt")}} ] && [ ! -r {{Quote(canaries + "/credential.txt")}} ] || exit 70
            [ -z "${{{credentialVariable}}:-}" ] || exit 70
            printf 'HOST_AND_CREDENTIAL_CANARIES_DENIED\n'
            /bin/mkdir /workspace
            /bin/mkdir /workspace/tests
            /usr/bin/printf '%s' '{{Convert.ToBase64String(Encoding.UTF8.GetBytes(Unix(sample)))}}' | /usr/bin/base64 -d > /workspace/sample.sh
            printf '#!/bin/sh\nexit 1\n' > /workspace/tests/check.sh
            /bin/chown -R 65534:65534 /workspace
            /bin/chmod 0777 /workspace
            /bin/mkdir -p /opt/comparemodels-evaluation /run/comparemodels-evaluation
            /usr/bin/printf '%s' '{{Convert.ToBase64String(Encoding.UTF8.GetBytes(check))}}' | /usr/bin/base64 -d > /opt/comparemodels-evaluation/check.sh
            printf '42\n' > /run/comparemodels-evaluation/expected
            expected_hash="$(/usr/bin/sha256sum /run/comparemodels-evaluation/expected | /usr/bin/cut -d ' ' -f 1)"
            /bin/chmod 0555 /opt/comparemodels-evaluation
            /bin/chmod {{(options.WeakenCheckPermissions ? "0777" : "0555")}} /opt/comparemodels-evaluation/check.sh
            /bin/chmod 0700 /run/comparemodels-evaluation
            /bin/chmod 0444 /run/comparemodels-evaluation/expected
            if {{drop}} /bin/sh -c {{Quote(Unix(WslProtectedEvaluationRunner.PrivilegeProbe) + "\n/bin/grep -q '^CapPrm:[[:space:]]*0000000000000000$' /proc/self/status")}}; then
                printf 'PRIVILEGE_DROP_VERIFIED\n'
            else
                printf 'PRIVILEGE_DROP_UNVERIFIED\n'
                exit 70
            fi
            if {{string.Join(' ', DropArguments)}} /bin/sh -c {{Quote(Unix(WslProtectedEvaluationRunner.ProtectionProbe))}}; then
                printf 'CHECK_PROTECTION_VERIFIED\n'
            else
                exit 70
            fi
            {{integrity}} || exit 70
            printf 'MXC_BOUNDARY_READY\n'
            IFS= read -r gate
            [ "$gate" = go ] || exit 70
            check={{(options.MissingCheck ? "/opt/comparemodels-evaluation/missing-check" : "/opt/comparemodels-evaluation/check.sh")}}
            if [ ! -f "$check" ] || [ ! -x "$check" ] || [ -L "$check" ]; then
                printf 'MXC_CHECK_UNRUNNABLE\n'
                printf 'MXC_RESULT_READY\n'
                while :; do /bin/sleep 1; done
            fi
            set +e
            "$check" </dev/null
            status=$?
            set -e
            printf 'MXC_CHECK_END=%s\n' "$status"
            {{(options.OmitFinalIntegrity ? "" : integrity + " || exit 70\nprintf 'MXC_INTEGRITY_AFTER\\n'")}}
            printf 'MXC_RESULT_READY\n'
            while :; do /bin/sleep 1; done
            """);
    }

    private static async Task<string> VerifyEffectiveBoundaryAsync(string session, MxcEvaluationEvidence evidence)
    {
        var list = await RunDaemonCommandAsync(session, ["docker", "ps", "-aq", "--no-trunc"], TimeSpan.FromSeconds(15));
        evidence.DaemonInspection = $"List exit={list.ExitCode}; output={list.StandardOutput.Trim()}; error={list.StandardError.Trim()}";
        string[] ids = list.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.ExitCode != 0 || ids.Length != 1 || ids[0].Length != 64 || ids[0].Any(c => !Uri.IsHexDigit(c)))
        {
            throw new MxcBoundaryException("The MXC-owned session did not contain exactly one verifiable evaluator.");
        }
        var inspect = await RunDaemonCommandAsync(session, ["docker", "inspect", ids[0]], TimeSpan.FromSeconds(15));
        if (inspect.ExitCode != 0) throw new MxcBoundaryException($"Effective inspection failed: {inspect.StandardError}");
        using var json = JsonDocument.Parse(inspect.StandardOutput);
        var root = json.RootElement[0];
        evidence.ContainerInspect = root.Clone();
        if (!HasMountlessBoundary(root, ids[0]))
        {
            throw new MxcBoundaryException("The effective image, privileged mode, network, or zero-mount boundary did not match the request.");
        }
        evidence.EffectivePrivileged = "false";
        evidence.ImageId = root.GetProperty("Image").GetString();
        return ids[0];
    }

    internal static bool HasMountlessBoundary(JsonElement root, string containerId)
    {
        var host = root.GetProperty("HostConfig");
        return root.GetProperty("Id").GetString() == containerId
            && root.GetProperty("Image").GetString() == $"sha256:{MxcImage.ConfigSha256}"
            && host.GetProperty("Privileged").ValueKind == JsonValueKind.False
            && host.GetProperty("NetworkMode").GetString() == "none"
            && root.GetProperty("Mounts").GetArrayLength() == 0;
    }

    internal static async Task<T> BoundedAsync<T>(
        Func<Task<T>> operation, TimeSpan limit, Action<Task>? onStart = null, CancellationToken token = default)
    {
        Task<T> pending = Task.Run(operation);
        onStart?.Invoke(pending);
        _ = pending.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return await pending.WaitAsync(limit, token);
    }

    internal static async Task BoundedAsync(
        Func<Task> operation, TimeSpan limit, Action<Task>? onStart = null, CancellationToken token = default)
    {
        Task pending = Task.Run(operation);
        onStart?.Invoke(pending);
        _ = pending.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        await pending.WaitAsync(limit, token);
    }

    internal static async Task<byte[]> FreezeAndCaptureAsync(
        Func<Task> pause, Func<Task<bool>> isPaused, Func<Task<byte[]>> capture,
        Func<Task> startKill, TimeSpan operationLimit, Action? onVerified = null,
        Action<Task>? onOperationStarted = null)
    {
        try
        {
            using var freezeDeadline = new CancellationTokenSource(FreezeOperationLimit);
            try
            {
                await BoundedAsync(pause, operationLimit, onOperationStarted, freezeDeadline.Token);
                if (!await BoundedAsync(isPaused, operationLimit, onOperationStarted, freezeDeadline.Token))
                    throw new IOException("The evaluator pause was not verified.");
            }
            catch (OperationCanceledException) when (freezeDeadline.IsCancellationRequested)
            {
                throw new TimeoutException("The evaluator pause and verification exceeded the freeze deadline.");
            }
            onVerified?.Invoke();
            return await BoundedAsync(capture, operationLimit, onOperationStarted);
        }
        finally
        {
            _ = startKill();
        }
    }

    private static async Task RunDaemonCheckedAsync(string session, string[] arguments, string operation)
    {
        var result = await RunDaemonCommandAsync(session, arguments, TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0) throw new IOException($"Could not {operation} the evaluator: {result.StandardError}");
    }

    private static async Task<byte[]> CaptureArchiveAsync(string session, string container, bool failCapture)
    {
        var result = await RunDaemonCommandAsync(session,
            ["sh", "-c", "set -o pipefail; docker cp \"$1:$2\" - | base64 -w0", "copy-project",
                container, failCapture ? "/missing-workspace" : "/workspace"],
            TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0) throw new IOException($"Could not capture the evaluator project: {result.StandardError}");
        try { return Convert.FromBase64String(result.StandardOutput); }
        catch (FormatException exception) { throw new IOException("The project archive was not valid base64.", exception); }
    }

    internal static void ValidateArchive(byte[] archive)
    {
        const long maxBytes = 1024 * 1024;
        if (archive.Length == 0 || archive.Length > maxBytes)
            throw new IOException("The project archive is empty or exceeds its size limit.");
        using var stream = new MemoryStream(archive, writable: false);
        using var reader = new TarReader(stream);
        int count = 0;
        long total = 0;
        bool rootFound = false, sampleFound = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            string name = entry.Name.TrimEnd('/');
            string[] segments = name.Split('/');
            if (++count > 1000 || name.Length == 0 || name.Length > 512
                || name.Contains('\\') || segments[0] != "workspace"
                || segments.Any(segment => segment is "" or "." or "..")
                || !names.Add(name))
                throw new IOException("The project archive has an unsafe or duplicate entry path.");
            if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile
                or TarEntryType.V7RegularFile or TarEntryType.SymbolicLink))
                throw new IOException("The project archive contains an unsupported entry type.");
            total += entry.Length;
            if (total > maxBytes) throw new IOException("The project archive contents exceed their size limit.");
            rootFound |= name == "workspace" && entry.EntryType == TarEntryType.Directory;
            sampleFound |= name == "workspace/sample.sh"
                && entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile;
        }
        if (!rootFound || !sampleFound) throw new IOException("The project archive is missing its workspace or sample.");
    }

    internal static async Task<Task<string>> WaitForMountlessResultAsync(
        Task wait, Func<Task<string>> observeDescendants, TimeSpan limit,
        CancellationToken cancellationToken, MxcEvaluationEvidence evidence,
        Action<Task>? onObservationStarted = null)
    {
        using var timeout = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        return await WaitForMountlessResultAsync(wait, observeDescendants, linked.Token,
            cancellationToken, evidence, onObservationStarted, null, null, null);
    }

    private static async Task<Task<string>> WaitForMountlessResultAsync(
        Task wait, Func<Task<string>> observeDescendants, CancellationToken deadlineToken,
        CancellationToken cancellationToken, MxcEvaluationEvidence evidence,
        Action<Task>? onObservationStarted, Task? observationReady, Task? observationStop,
        Action<string>? onObservationCompleted)
    {
        // Starting the inspection process is synchronous and must not delay cancellation.
        Task<string> observation = Task.Run(async () =>
        {
            if (observationReady is not null)
            {
                Task first = observationStop is null
                    ? await Task.WhenAny(observationReady, wait).WaitAsync(deadlineToken)
                    : await Task.WhenAny(observationReady, wait, observationStop).WaitAsync(deadlineToken);
                if (first != observationReady || wait.IsCompleted || observationStop?.IsCompleted == true)
                    throw new IOException("The heartbeat child was not ready before the evaluator stopped.");
                await observationReady;
                deadlineToken.ThrowIfCancellationRequested();
            }
            string snapshot = await observeDescendants();
            if (observationReady is not null && (wait.IsCompleted || observationStop?.IsCompleted == true))
                throw new IOException("The heartbeat observation completed after the evaluator stopped.");
            onObservationCompleted?.Invoke(snapshot);
            return snapshot;
        });
        onObservationStarted?.Invoke(observation);
        _ = observation.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            deadlineToken.ThrowIfCancellationRequested();
            await wait.WaitAsync(deadlineToken);
            deadlineToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
        {
            evidence.Outcome = cancellationToken.IsCancellationRequested
                ? EvaluationOutcome.Cancelled : EvaluationOutcome.TimedOut;
        }
        return observation;
    }

    internal static async Task<MountlessWaitResult> ReleaseAndWaitAsync(
        Func<Task> release, Task wait, Func<Task<string>> observeDescendants,
        TimeSpan limit, CancellationToken cancellationToken, Action<Task> onReleaseStarted,
        Action<Task> onObservationStarted, Func<Task> startKill, MxcEvaluationEvidence evidence,
        Task? observationReady = null, Task? observationStop = null, Action<string>? onObservationCompleted = null)
    {
        using var timeout = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            Task gateRelease = Task.Run(release);
            onReleaseStarted(gateRelease);
            _ = gateRelease.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await gateRelease.WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            evidence.Outcome = cancellationToken.IsCancellationRequested
                ? EvaluationOutcome.Cancelled : EvaluationOutcome.TimedOut;
            evidence.OutcomeBeforeCapture = evidence.Outcome;
            evidence.CaptureError = "The gate release was interrupted; the project could not be captured safely.";
            evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
            _ = startKill();
            return new(null, true);
        }
        Task<string> observation = await WaitForMountlessResultAsync(
            wait, observeDescendants, linked.Token, cancellationToken, evidence, onObservationStarted,
            observationReady, observationStop, onObservationCompleted);
        return new(observation, false);
    }

    internal static Task StartKillAsync(Action kill, Task? wait)
    {
        Task operation = Task.Run(() =>
        {
            try { kill(); }
            catch (MxcException) when (wait is { IsCompletedSuccessfully: true }) { }
        });
        _ = operation.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return operation;
    }

    internal static bool CanRemoveCanaries(
        bool canarySessionAbsent, bool evaluatorSessionAbsent, bool processDisposed, IEnumerable<Task> ownedOperations) =>
        canarySessionAbsent && evaluatorSessionAbsent && processDisposed
        && ownedOperations.All(task => task.IsCompleted);

    internal static async Task<bool> FinishProcessAsync(
        Task kill, Task? wait, Task? output, Task? error, TimeSpan killLimit, TimeSpan drainLimit,
        List<string> errors)
    {
        try { await kill.WaitAsync(killLimit); }
        catch (Exception exception) { errors.Add($"Process stop: {exception.Message}"); }
        bool drained = true;
        try { if (wait is not null) await wait.WaitAsync(drainLimit); }
        catch (Exception exception) { errors.Add($"Process wait: {exception.Message}"); drained = false; }
        try
        {
            Task[] readers = new[] { output, error }.OfType<Task>().ToArray();
            if (readers.Length > 0) await Task.WhenAll(readers).WaitAsync(drainLimit);
        }
        catch (Exception exception) { errors.Add($"Output drain: {exception.Message}"); drained = false; }
        return drained;
    }

    internal static async Task RecordDescendantObservationAsync(
        Task<string> observation, string? requiredMarker, MxcEvaluationEvidence evidence)
    {
        try
        {
            evidence.DescendantBeforeStop = await observation.WaitAsync(TimeSpan.FromSeconds(1));
            if (requiredMarker is not null && !HasSampleDescendant(evidence.DescendantBeforeStop, requiredMarker))
                throw new IOException("The marked heartbeat child and its sample parent were not verified.");
        }
        catch (Exception exception)
        {
            string message = $"Descendant observation: {exception.Message}";
            if (requiredMarker is null) evidence.Warnings.Add(message);
            else
            {
                evidence.DescendantObservationError = message;
                evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
            }
        }
    }

    internal static bool HasSampleDescendant(string snapshot, string marker)
    {
        var processes = new Dictionary<int, (int Parent, string Command)>();
        foreach (string line in snapshot.Split('\n'))
        {
            string[] columns = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length != 3 || !int.TryParse(columns[0], out int pid)
                || !int.TryParse(columns[1], out int parent) || pid <= 0 || parent < 0)
                continue;
            if (!processes.TryAdd(pid, (parent, columns[2].Trim()))) return false;
        }
        int[] samplePids = processes
            .Where(process => process.Value.Command == "/bin/sh /workspace/sample.sh")
            .Select(process => process.Key).ToArray();
        if (samplePids.Length != 1) return false;
        int samplePid = samplePids[0];
        return processes.Count(process =>
        {
            if (process.Key == samplePid || process.Value.Parent != samplePid) return false;
            string[] arguments = process.Value.Command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return arguments.Length >= 4 && arguments[0] == "/bin/sh" && arguments[1] == "-c"
                && arguments[^1] == marker;
        }) == 1;
    }

    private static async Task<string> ObserveDescendantsAsync(string session, string containerId)
    {
        var result = await RunDaemonCommandAsync(
            session, ["docker", "top", containerId, "-ww", "-eo", "pid,ppid,args"], TimeSpan.FromSeconds(10));
        if (result.ExitCode != 0) throw new IOException($"Descendants could not be observed: {result.StandardError}");
        return result.StandardOutput;
    }

    private static async Task VerifyCanaryControlAsync(
        string runId, string directory, string canaries, MxcEvaluationEvidence evidence)
    {
        string name = $"{runId}-canary-control";
        string storage = Path.Combine(directory, "control-store");
        var request = CreateRequest(name, canaries, canaries, storage,
            $"/bin/cat {Quote(ToWslPath(Path.Combine(canaries, "host.txt")))} " +
            Quote(ToWslPath(Path.Combine(canaries, "credential.txt"))));
        request.Filesystem = new() { ReadonlyPaths = { canaries } };
        request.WorkingDirectory = canaries;
        request.TimeoutMs = 20_000;
        try
        {
            var result = MxcContainer.Run(request);
            evidence.CanaryControlOutput = result.Stdout;
            evidence.Warnings.AddRange(result.Warnings);
            if (result.TimedOut || result.ExitCode != 0
                || result.Stdout != "synthetic host canarysynthetic credential canary")
                throw new MxcBoundaryException("The mounted positive host/credential canary control was not verified.");
            evidence.CanaryControlVerified = true;
        }
        finally
        {
            try
            {
                if (await SessionExistsAsync(name))
                    throw new IOException("The canary-control session remained active.");
                evidence.CanaryControlSessionAbsent = true;
                if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);
            }
            catch (Exception exception)
            {
                evidence.CleanupErrors.Add($"Canary-control cleanup: {exception.Message}");
            }
        }
    }

    private static async Task<bool> SessionExistsAsync(string session)
    {
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wslc.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in new[] { "system", "session", "list" }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Session listing did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (process.ExitCode != 0) throw new IOException($"Session listing failed: {await error}");
            return (await output).Contains(session, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static Dictionary<string, string> ReadNativeIdentities()
    {
        var result = new Dictionary<string, string>();
        foreach (string file in new[] { "mxc_ffi.dll", "wslcsdk.dll", "wxc-wslc-daemon.exe", "mxc-wslc-gateway.tar" })
        {
            string path = Path.Combine(AppContext.BaseDirectory, file);
            using var stream = File.OpenRead(path);
            result[file] = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        foreach (ProcessModule module in currentProcess.Modules)
        {
            if (module.ModuleName is "mxc_ffi.dll" or "wslcsdk.dll")
            {
                using var stream = File.OpenRead(module.FileName);
                string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (hash != result[module.ModuleName])
                    throw new MxcBoundaryException($"The loaded {module.ModuleName} did not match the packaged asset.");
            }
        }
        if (result["mxc_ffi.dll"] != "e7ba97ff834bd7a3921a0d0292735cb807a5967013141da540c596bbe59f5bf2"
            || result["wslcsdk.dll"] != "b17d70b14c0ade45b1430f01fad8433269f0536f7dee90c019b5f2daf3bc46c2")
            throw new MxcBoundaryException("The native files do not match the pinned Microsoft.Mxc.Sdk 1.1.0 package.");
        return result;
    }

    private static int[] ReadDaemonPids()
    {
        var ids = new List<int>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("wxc-wslc-daemon"))
        {
            using (process) ids.Add(process.Id);
        }
        return ids.Order().ToArray();
    }

    private static async Task ReadAsync(Stream stream, CapturedOutput output, Action? afterRead = null)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            output.Append(new string(buffer, 0, count), afterRead);
        }
    }

    internal static bool HasLine(string text, string marker) => text
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n').Contains(marker, StringComparer.Ordinal);
    internal static bool HasCompleteLine(string text, string marker) =>
        ("\n" + text.Replace("\r\n", "\n", StringComparison.Ordinal))
            .Contains($"\n{marker}\n", StringComparison.Ordinal);
    private static string Quote(string text) => $"'{text.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    private static string Unix(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class CapturedOutput(Action<string>? observer)
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _text = new();
        public bool Truncated { get; private set; }
        public string Text { get { using (_gate.EnterScope()) return _text.ToString(); } }
        public void Append(string text, Action? afterAppend = null)
        {
            using (_gate.EnterScope())
            {
                int remaining = 1024 * 1024 - _text.Length;
                _text.Append(text.AsSpan(0, Math.Min(remaining, text.Length)));
                Truncated |= text.Length > remaining;
            }
            afterAppend?.Invoke();
            observer?.Invoke(text);
        }
    }
    private sealed class MxcBoundaryException(string message) : Exception(message);
}

internal readonly record struct MountlessWaitResult(Task<string>? Observation, bool GateInterrupted);

internal sealed record MxcEvaluationOptions(
    EvaluationSample Sample,
    bool MissingCheck = false,
    bool WeakenCheckPermissions = false,
    bool OmitBoundingDrop = false,
    bool SubjectExit126 = false,
    bool OmitFinalIntegrity = false,
    bool FailCapture = false,
    bool LongRunning = false,
    TimeSpan? Limit = null,
    Action<string>? Output = null,
    Action? DescendantObserved = null);

internal sealed class MxcEvaluationEvidence
{
    public string RunId { get; init; } = "";
    public string Package { get; init; } = "Microsoft.Mxc.Sdk";
    public string PackageVersion { get; init; } = "1.1.0";
    public string RequestContract { get; init; } = "ContainerRequest / stable 1.0.0 one-shot writer";
    public string SelectedBackend { get; init; } = "wslc";
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public EvaluationSample Sample { get; init; }
    public EvaluationOutcome Outcome { get; set; } = EvaluationOutcome.Blocked;
    public EvaluationOutcome? OutcomeBeforeCapture { get; set; }
    public string Windows { get; init; } = "";
    public string DotNet { get; init; } = "";
    public string? NativeVersion { get; set; }
    public string? ManagedVersion { get; set; }
    public Dictionary<string, string>? NativeFiles { get; set; }
    public JsonElement? Platform { get; set; }
    public JsonElement? Backends { get; set; }
    public string? WslVersion { get; set; }
    public string? WslContainerService { get; set; }
    public string? Prerequisites { get; set; }
    public string SessionName { get; init; } = "";
    public string StoragePath { get; init; } = "";
    public bool DaemonUsed { get; init; } = false;
    public int[] DaemonPidsBefore { get; set; } = [];
    public int[] DaemonPidsAfter { get; set; } = [];
    public bool NoNewDaemonObserved { get; set; }
    public bool SessionAbsentAfterCleanup { get; set; }
    public bool RequiredTelemetryAccepted { get; init; }
    public string TelemetryMechanism { get; init; } = "Required ETW events; no supported packaged opt-out; Windows routing unverified";
    public string? TelemetryConsent { get; set; }
    public string? TelemetryPolicy { get; set; }
    public string ImageReference { get; init; } = "";
    public string? ImageArchiveSha256 { get; set; }
    public string? ImageId { get; set; }
    public JsonElement? Request { get; set; }
    public JsonElement? WslcRequest { get; set; }
    public JsonElement? ContainerInspect { get; set; }
    public string? DaemonInspection { get; set; }
    public string? EffectivePrivileged { get; set; }
    public bool BoundaryVerified { get; set; }
    public bool PauseVerified { get; set; }
    public bool CanaryControlVerified { get; set; }
    public bool CanaryControlSessionAbsent { get; set; }
    public string? CanaryControlOutput { get; set; }
    public string? CheckSha256 { get; set; }
    public bool CheckStarted { get; set; }
    public int? CheckExitCode { get; set; }
    public bool NativeTimedOut { get; set; }
    public double EvaluationLimitMilliseconds { get; init; }
    public double? EvaluationElapsedMilliseconds { get; set; }
    public string StandardOutput { get; set; } = "";
    public string StandardError { get; set; } = "";
    public bool OutputTruncated { get; set; }
    public string? DescendantMarker { get; init; }
    public string? DescendantBeforeStop { get; set; }
    public string? DescendantObservationError { get; set; }
    public bool HeartbeatStopped { get; set; }
    public string? ProjectArchivePath { get; set; }
    public string? CaptureError { get; set; }
    public string? Error { get; set; }
    public string? NativeErrorCode { get; set; }
    public string? NativeOperation { get; set; }
    public string? NativeRemediation { get; set; }
    public List<string> Warnings { get; } = [];
    public List<string> CleanupErrors { get; } = [];
    public List<string> PersistenceErrors { get; } = [];
}
