using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;
using WslContainerFeasibility;
using static WslContainerFeasibility.ContainerOperations;

namespace MxcWslcFeasibility;

public sealed record MxcEvaluationResult(
    EvaluationOutcome Outcome, string Summary, string EvidencePath, string? ProjectDirectoryPath);

public sealed class MxcEvaluationRunner
{
    private static readonly string[] DropArguments =
    [
        "/usr/bin/setpriv", "--reuid=65534", "--regid=65534", "--clear-groups",
        "--inh-caps=-all", "--ambient-caps=-all", "--bounding-set=-all", "--no-new-privs"
    ];

    public Task<MxcEvaluationResult> RunAsync(
        EvaluationSample sample, bool acceptRequiredTelemetry, CancellationToken cancellationToken = default) =>
        RunForTestAsync(new(sample), acceptRequiredTelemetry, cancellationToken);

    internal async Task<MxcEvaluationResult> RunForTestAsync(
        MxcEvaluationOptions options, bool acceptRequiredTelemetry, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(options.Sample)) throw new ArgumentOutOfRangeException(nameof(options));
        TimeSpan limit = options.Limit ?? TimeSpan.FromSeconds(15);
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        string id = $"CompareModels-MxcEvaluation-{Guid.NewGuid():N}";
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new IOException("Local application data is unavailable.");
        string directory = Path.Combine(local, "compareModels", "Feasibility", "MxcWslc", id);
        string workspace = Path.Combine(directory, "workspace");
        string protectedDirectory = Path.Combine(directory, "protected");
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
            ImageReference = WslProtectedEvaluationRunner.EvaluationImage
        };
        MxcProcess? process = null;
        bool spawnAttempted = false;
        bool workspaceCreated = false;
        bool variableSet = false;
        string? evidencePath = null;
        string? containerId = null;
        string? projectPath = null;
        var output = new CapturedOutput(options.Output);
        var error = new CapturedOutput(options.Output);
        Task<WaitResult>? wait = null;
        Task? readOutput = null;
        Task? readError = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(protectedDirectory);
            Directory.CreateDirectory(canaries);
            workspaceCreated = true;
            string sample = options.LongRunning
                ? """
                  #!/bin/sh
                  /bin/sh -c 'while :; do printf x >> /workspace/heartbeat; printf "HEARTBEAT\n" >&2; /bin/sleep 0.1; done' child &
                  printf 'CHILD_READY\n' >&2
                  wait
                  """
                : WslProtectedEvaluationRunner.CreateSample(
                    new(options.Sample, SubjectExit126: options.SubjectExit126), id);
            await File.WriteAllTextAsync(Path.Combine(workspace, "sample.sh"), Unix(sample), cancellationToken);
            Directory.CreateDirectory(Path.Combine(workspace, "tests"));
            await File.WriteAllTextAsync(Path.Combine(workspace, "tests", "check.sh"), "#!/bin/sh\nexit 1\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(canaries, "host.txt"), "synthetic host canary", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(canaries, "credential.txt"), "synthetic credential canary", cancellationToken);
            Environment.SetEnvironmentVariable(credentialVariable, "synthetic credential canary");
            variableSet = true;
            await VerifyCanaryControlAsync(id, directory, canaries, evidence);
            string check = Unix(WslProtectedEvaluationRunner.CheckScript);
            evidence.CheckSha256 = Hash(check);
            await File.WriteAllTextAsync(Path.Combine(protectedDirectory, "check.sh"), check, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(protectedDirectory, "start.sh"),
                CreateOrchestrator(options, credentialVariable, ToWslPath(canaries)), cancellationToken);

            var request = CreateRequest(
                id, workspace, protectedDirectory, storage,
                $"/bin/sh {Quote(ToWslPath(Path.Combine(protectedDirectory, "start.sh")))} " +
                $"{Quote(ToWslPath(workspace))} {Quote(ToWslPath(protectedDirectory))}");
            evidence.Request = JsonSerializer.SerializeToElement(request);
            evidence.WslcRequest = JsonSerializer.SerializeToElement((Containment.Wslc)request.Containment);
            spawnAttempted = true;
            process = MxcContainer.Spawn(request);
            (process.StandardInput ?? throw new IOException("MXC did not expose standard input.")).Close();
            Stream stdout = process.StandardOutput ?? throw new IOException("MXC did not expose standard output.");
            Stream stderr = process.StandardError ?? throw new IOException("MXC did not expose standard error.");
            readOutput = ReadAsync(stdout, output, () =>
            {
                if (HasLine(output.Text, "MXC_BOUNDARY_READY")) ready.TrySetResult();
            });
            readError = ReadAsync(stderr, error);
            wait = process.WaitAsync();
            Task first = await Task.WhenAny(ready.Task, wait).WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
            if (first == ready.Task)
            {
                containerId = await VerifyEffectiveBoundaryAsync(id, workspace, protectedDirectory, evidence);
                evidence.BoundaryVerified = true;
                var stopwatch = Stopwatch.StartNew();
                Task<string> descendantObservation;
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(workspace, "go"), "trusted host gate", cancellationToken);
                    descendantObservation = ObserveDescendantsAsync(id, containerId);
                    _ = descendantObservation.ContinueWith(
                        static task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    using var timeout = new CancellationTokenSource(limit);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
                    try
                    {
                        await wait.WaitAsync(linked.Token);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        evidence.Outcome = cancellationToken.IsCancellationRequested
                            ? EvaluationOutcome.Cancelled : EvaluationOutcome.TimedOut;
                        process.Kill();
                        await wait.WaitAsync(TimeSpan.FromSeconds(60));
                        try
                        {
                            evidence.DescendantBeforeStop = await descendantObservation.WaitAsync(TimeSpan.FromSeconds(1));
                        }
                        catch (Exception exception)
                        {
                            evidence.Warnings.Add($"Descendant observation: {exception.Message}");
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
            await Task.WhenAll(readOutput, readError).WaitAsync(TimeSpan.FromSeconds(60));
            var result = await wait;
            evidence.CheckExitCode = result.ExitCode;
            evidence.NativeTimedOut = result.TimedOut;
            evidence.OutputTruncated = output.Truncated || error.Truncated;
            if (evidence.Outcome is not (EvaluationOutcome.Cancelled or EvaluationOutcome.TimedOut))
            {
                evidence.Outcome = Classify(result, output.Text, evidence.BoundaryVerified, evidence.OutputTruncated);
            }
            if (options.Sample == EvaluationSample.Tampered && evidence.Outcome == EvaluationOutcome.Failed
                && new[] { "DIRECT_WRITE_DENIED", "SYMLINK_WRITE_DENIED", "CHECK_RENAME_DENIED", "PARENT_FD_WRITE_DENIED" }
                    .Any(marker => !HasLine(error.Text, marker)))
            {
                throw new MxcBoundaryException("The tampering controls were incomplete or unverified.");
            }
            if (evidence.Outcome == EvaluationOutcome.Blocked)
            {
                evidence.Error = "The trusted in-guest boundary or integrity controls failed or were unverified.";
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
            evidence.OutcomeBeforeCapture = evidence.Outcome;
            if (process is not null)
            {
                try
                {
                    if (!process.TryGetExitCode(out _)) process.Kill();
                    if (wait is not null) await wait.WaitAsync(TimeSpan.FromSeconds(60));
                    if (readOutput is not null && readError is not null)
                        await Task.WhenAll(readOutput, readError).WaitAsync(TimeSpan.FromSeconds(60));
                    evidence.Warnings.AddRange(process.Warnings);
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
            try
            {
                evidencePath = await WslFixtureRunner.SaveEvidenceAsync(local, id, evidence);
            }
            catch (Exception exception)
            {
                evidence.PersistenceErrors.Add($"Before cleanup: {exception.Message}");
                evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
            }
            if (process is not null)
            {
                try { process.Dispose(); }
                catch (Exception exception) { evidence.CleanupErrors.Add($"Process disposal: {exception.Message}"); }
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
                    string heartbeat = Path.Combine(workspace, "heartbeat");
                    long length = new FileInfo(heartbeat).Length;
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    evidence.HeartbeatStopped = length > 0 && new FileInfo(heartbeat).Length == length
                        && evidence.SessionAbsentAfterCleanup;
                    if (!evidence.HeartbeatStopped)
                        throw new IOException("Descendant termination was not confirmed by the heartbeat and session checks.");
                }
                catch (Exception exception)
                {
                    evidence.CleanupErrors.Add($"Descendant verification: {exception.Message}");
                }
            }
            if (workspaceCreated)
            {
                try
                {
                    string gate = Path.Combine(workspace, "go");
                    if (File.Exists(gate)) File.Delete(gate);
                    if (options.FailCapture) throw new IOException("Synthetic project-capture failure.");
                    if (!Directory.Exists(workspace)
                        || !File.Exists(Path.Combine(workspace, "sample.sh")))
                        throw new IOException("The resulting project did not remain accessible after cleanup.");
                    projectPath = workspace;
                    evidence.ProjectDirectoryPath = workspace;
                }
                catch (Exception exception)
                {
                    projectPath = null;
                    evidence.CaptureError = exception.Message;
                    evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
                }
            }
            if (variableSet) WslFixtureRunner.CaptureCleanupError(evidence.CleanupErrors, "environment canary",
                () => Environment.SetEnvironmentVariable(credentialVariable, null));
            if (Directory.Exists(canaries)) WslFixtureRunner.CaptureCleanupError(
                evidence.CleanupErrors, "host canaries", () => Directory.Delete(canaries, recursive: true));
            if (evidence.CleanupErrors.Count > 0) evidence.Outcome = EvaluationOutcome.InfrastructureFailure;
            evidence.CompletedUtc = DateTimeOffset.UtcNow;
            try
            {
                evidencePath = await WslFixtureRunner.SaveEvidenceAsync(local, id, evidence, evidencePath is not null);
            }
            catch (Exception exception)
            {
                throw new IOException($"Final MXC evidence could not be persisted; pre-cleanup evidence: " +
                    $"{evidencePath ?? "unavailable"}; retained project: {projectPath ?? workspace}; " +
                    $"cleanup errors: {string.Join("; ", evidence.CleanupErrors)}.", exception);
            }
        }
        var errors = new List<string>();
        if (evidence.Error is not null) errors.Add(evidence.Error);
        if (evidence.CaptureError is not null) errors.Add(evidence.CaptureError);
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

    internal static EvaluationOutcome Classify(WaitResult result, string output, bool boundary, bool truncated)
    {
        if (!boundary || truncated) return EvaluationOutcome.Blocked;
        if (result.TimedOut) return EvaluationOutcome.InfrastructureFailure;
        if (result.ExitCode == 11 && HasLine(output, "MXC_CHECK_UNRUNNABLE")) return EvaluationOutcome.Unrunnable;
        if (!HasLine(output, "MXC_INTEGRITY_AFTER")) return EvaluationOutcome.Blocked;
        if (!HasLine(output, "SETUP_READY")) return EvaluationOutcome.InfrastructureFailure;
        return result.ExitCode switch
        {
            0 when HasLine(output, "MXC_CHECK_END=0") => EvaluationOutcome.Passed,
            1 when HasLine(output, "MXC_CHECK_END=1") => EvaluationOutcome.Failed,
            _ => EvaluationOutcome.InfrastructureFailure
        };
    }

    private static string CreateOrchestrator(MxcEvaluationOptions options, string credentialVariable, string canaries)
    {
        string drop = string.Join(' ', DropArguments.Where(argument =>
            !options.OmitBoundingDrop || argument != "--bounding-set=-all"));
        string integrity = """
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
            /usr/bin/cmp -s "$protected/check.sh" /opt/comparemodels-evaluation/check.sh &&
            [ "$(/usr/bin/sha256sum /run/comparemodels-evaluation/expected | /usr/bin/cut -d ' ' -f 1)" = "$expected_hash" ]
            """;
        return Unix($$"""
            #!/bin/sh
            set -eu
            work="$1"
            protected="$2"
            /usr/bin/id
            /bin/uname -a
            /bin/cat /proc/self/mountinfo
            [ "$(/bin/ls /sys/class/net)" = lo ] || { printf 'EXTERNAL_INTERFACE_PRESENT\n'; exit 70; }
            printf 'NETWORK_NONE_VERIFIED\n'
            [ ! -r '{{canaries}}/host.txt' ] && [ ! -r '{{canaries}}/credential.txt' ] || exit 70
            [ -z "${{{credentialVariable}}:-}" ] || exit 70
            printf 'HOST_AND_CREDENTIAL_CANARIES_DENIED\n'
            /bin/ln -s "$work" /workspace
            /bin/mkdir -p /opt/comparemodels-evaluation /run/comparemodels-evaluation
            /bin/cp "$protected/check.sh" /opt/comparemodels-evaluation/check.sh
            printf '42\n' > /run/comparemodels-evaluation/expected
            expected_hash="$(/usr/bin/sha256sum /run/comparemodels-evaluation/expected | /usr/bin/cut -d ' ' -f 1)"
            /bin/chmod 0555 /opt/comparemodels-evaluation
            /bin/chmod {{(options.WeakenCheckPermissions ? "0777" : "0555")}} /opt/comparemodels-evaluation/check.sh
            /bin/chmod 0700 /run/comparemodels-evaluation
            /bin/chmod 0444 /run/comparemodels-evaluation/expected
            if (printf 'root-write\n' > "$protected/check.sh") 2>/dev/null; then
                printf 'READONLY_SOURCE_WRITE_SUCCEEDED\n'
                exit 70
            fi
            printf 'READONLY_SOURCE_WRITE_DENIED\n'
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
            while [ ! -f "$work/go" ]; do /bin/sleep 0.05; done
            check={{(options.MissingCheck ? "/opt/comparemodels-evaluation/missing-check" : "/opt/comparemodels-evaluation/check.sh")}}
            if [ ! -f "$check" ] || [ ! -x "$check" ] || [ -L "$check" ]; then
                printf 'MXC_CHECK_UNRUNNABLE\n'
                exit 11
            fi
            set +e
            "$check"
            status=$?
            set -e
            printf 'MXC_CHECK_END=%s\n' "$status"
            {{(options.OmitFinalIntegrity ? "exit \"$status\"" : "")}}
            {{integrity}} || exit 70
            printf 'MXC_INTEGRITY_AFTER\n'
            exit "$status"
            """);
    }

    private static async Task<string> VerifyEffectiveBoundaryAsync(
        string session, string workspace, string protectedDirectory, MxcEvaluationEvidence evidence)
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
        var host = root.GetProperty("HostConfig");
        var mounts = root.GetProperty("Mounts").EnumerateArray().ToArray();
        if (root.GetProperty("Id").GetString() != ids[0]
            || root.GetProperty("Image").GetString() != $"sha256:{MxcImage.ConfigSha256}"
            || host.GetProperty("Privileged").ValueKind != JsonValueKind.False
            || host.GetProperty("NetworkMode").GetString() != "none"
            || mounts.Length != 2
            || !HasExpectedMount(mounts, ToWslPath(workspace), ToWslPath(workspace), writable: true)
            || !HasExpectedMount(mounts, ToWslPath(protectedDirectory), ToWslPath(protectedDirectory), writable: false))
        {
            throw new MxcBoundaryException("The effective image, privileged mode, network, or filesystem grants did not match the request.");
        }
        evidence.EffectivePrivileged = "false";
        evidence.ImageId = root.GetProperty("Image").GetString();
        return ids[0];
    }

    internal static bool HasExpectedMount(JsonElement[] mounts, string source, string destination, bool writable) =>
        mounts.Any(m => m.GetProperty("Type").GetString() == "bind"
            && m.GetProperty("Source").GetString() == source
            && m.GetProperty("Destination").GetString() == destination
            && m.GetProperty("RW").ValueKind == (writable ? JsonValueKind.True : JsonValueKind.False));

    private static async Task<string> ObserveDescendantsAsync(string session, string containerId)
    {
        var result = await RunDaemonCommandAsync(
            session, ["docker", "top", containerId, "-eo", "pid,ppid,args"], TimeSpan.FromSeconds(10));
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
            output.Append(new string(buffer, 0, count));
            afterRead?.Invoke();
        }
    }

    internal static bool HasLine(string text, string marker) => text
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n').Contains(marker, StringComparer.Ordinal);
    private static string Quote(string text) => $"'{text.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    private static string Unix(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class CapturedOutput(Action<string>? observer)
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _text = new();
        public bool Truncated { get; private set; }
        public string Text { get { using (_gate.EnterScope()) return _text.ToString(); } }
        public void Append(string text)
        {
            using (_gate.EnterScope())
            {
                int remaining = 1024 * 1024 - _text.Length;
                _text.Append(text.AsSpan(0, Math.Min(remaining, text.Length)));
                Truncated |= text.Length > remaining;
            }
            observer?.Invoke(text);
        }
    }
    private sealed class MxcBoundaryException(string message) : Exception(message);
}

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
    Action<string>? Output = null);

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
    public EvaluationOutcome OutcomeBeforeCapture { get; set; }
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
    public bool CanaryControlVerified { get; set; }
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
    public string? DescendantBeforeStop { get; set; }
    public bool HeartbeatStopped { get; set; }
    public string? ProjectDirectoryPath { get; set; }
    public string? CaptureError { get; set; }
    public string? Error { get; set; }
    public string? NativeErrorCode { get; set; }
    public string? NativeOperation { get; set; }
    public string? NativeRemediation { get; set; }
    public List<string> Warnings { get; } = [];
    public List<string> CleanupErrors { get; } = [];
    public List<string> PersistenceErrors { get; } = [];
}
