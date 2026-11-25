using System.Text;
using System.Text.Json;
using System.Formats.Tar;
using Microsoft.Mxc.Sdk.V1;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;
using Xunit;
using static WslContainerFeasibility.ContainerOperations;

namespace MxcWslcFeasibility.Tests;

public sealed class MxcEvaluationRunnerTests
{
    private readonly MxcEvaluationRunner _sut = new();

    [Theory]
    [InlineData("MXC_INTEGRITY_AFTER", true)]
    [InlineData("MXC_INTEGRITY_AFTER\r\n", true)]
    [InlineData("  MXC_INTEGRITY_AFTER", false)]
    [InlineData("subject_output:\n  MXC_INTEGRITY_AFTER", false)]
    [InlineData("MXC_INTEGRITY_AFTER ", false)]
    [InlineData("MXC_INTEGRITY_AFTER\r", false)]
    public void GivenOutputLine_WhenIntegrityMarkerIsChecked_OnlyExactLinesMatch(string output, bool expected) =>
        Assert.Equal(expected, MxcEvaluationRunner.HasLine(output, "MXC_INTEGRITY_AFTER"));

    [Fact]
    public void GivenEffectiveBoundary_WhenAnyMountIsPresent_ItIsRejected()
    {
        foreach (string mounts in new[]
        {
            "[]",
            """[{"Type":"bind","Source":"/mnt/c"}]""",
            """[{"Type":"bind","Source":"/mnt/{01234567-89ab-cdef-0123-456789abcdef}"}]""",
            """[{"Type":"volume","Source":"workspace"}]"""
        })
        {
            using var document = JsonDocument.Parse(
                $$"""{"Id":"id","Image":"sha256:{{MxcImage.ConfigSha256}}","HostConfig":{"Privileged":false,"NetworkMode":"none"},"Mounts":{{mounts}}}""");
            Assert.Equal(mounts == "[]", MxcEvaluationRunner.HasMountlessBoundary(document.RootElement, "id"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenBlockedObservationStartup_WhenCancelledOrTimedOut_ResultWaitReturns(bool timedOut)
    {
        using var cancellation = new CancellationTokenSource();
        using var releaseObservation = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new MxcEvaluationEvidence();
        Task<Task<string>> run = MxcEvaluationRunner.WaitForMountlessResultAsync(
            Task.Delay(Timeout.InfiniteTimeSpan), () =>
        {
            started.TrySetResult();
            releaseObservation.Wait();
            return Task.FromResult("observed");
        }, TimeSpan.FromSeconds(timedOut ? 1 : 30), cancellation.Token, evidence);
        Task<string>? observation = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!timedOut) cancellation.Cancel();
            observation = await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(observation.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            releaseObservation.Set();
            observation ??= await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(timedOut ? EvaluationOutcome.TimedOut : EvaluationOutcome.Cancelled, evidence.Outcome);
        Assert.Equal("observed", await observation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenObservationFailure_WhenCancelledOrTimedOut_ResultWaitReturns(bool timedOut)
    {
        using var cancellation = new CancellationTokenSource();
        var evidence = new MxcEvaluationEvidence();
        Task<Task<string>> run = MxcEvaluationRunner.WaitForMountlessResultAsync(Task.Delay(Timeout.InfiniteTimeSpan),
            () => Task.FromException<string>(new IOException("inspection failed")),
            TimeSpan.FromMilliseconds(timedOut ? 50 : 30_000), cancellation.Token, evidence);
        if (!timedOut) cancellation.Cancel();
        Task<string> observation = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(timedOut ? EvaluationOutcome.TimedOut : EvaluationOutcome.Cancelled, evidence.Outcome);
        await Assert.ThrowsAsync<IOException>(() => observation);
    }

    [Theory]
    [InlineData("pause", false)]
    [InlineData("pause", true)]
    [InlineData("verify", false)]
    [InlineData("verify", true)]
    [InlineData("capture", false)]
    [InlineData("capture", true)]
    public async Task GivenPauseOrCaptureFailure_WhenFreezing_KillStartsWithoutWaiting(string stage, bool stall)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new List<Task>();
        async Task FailOrStall()
        {
            if (stall) await release.Task;
            else throw new IOException("injected failure");
        }
        try
        {
            Task run = MxcEvaluationRunner.FreezeAndCaptureAsync(
                async () => { if (stage == "pause") await FailOrStall(); },
                async () => { if (stage == "verify") await FailOrStall(); return true; },
                async () => { if (stage == "capture") await FailOrStall(); return [1]; },
                () => { killed.TrySetResult(); return killCompletion.Task; },
                TimeSpan.FromMilliseconds(100), onOperationStarted: operations.Add);
            await Assert.ThrowsAnyAsync<Exception>(() => run);
            await killed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(killCompletion.Task.IsCompleted);
            Assert.Equal(stall, operations.Any(operation => !operation.IsCompleted));
        }
        finally
        {
            release.TrySetResult();
            killCompletion.TrySetResult();
        }
    }

    [Fact]
    public async Task GivenKillFault_WhenCleaningUp_ProcessAndReadersAreStillAwaited()
    {
        var processWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<string>();
        Task<bool> cleanup = MxcEvaluationRunner.FinishProcessAsync(
            Task.FromException(new IOException("kill failed")), processWait.Task, output.Task,
            null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), errors);
        try
        {
            await Task.Delay(100);
            Assert.False(cleanup.IsCompleted);
            processWait.TrySetResult();
            await Task.Delay(100);
            Assert.False(cleanup.IsCompleted);
        }
        finally
        {
            processWait.TrySetResult();
            output.TrySetResult();
            await cleanup;
        }
        Assert.Contains(errors, error => error.Contains("kill failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GivenStalledFreeze_WithProductionLimit_KillStartsPromptly(bool stallPause)
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool captured = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Task run = MxcEvaluationRunner.FreezeAndCaptureAsync(
            () => stallPause ? stalled.Task : Task.CompletedTask,
            async () => { if (!stallPause) await stalled.Task; return true; },
            () => { captured = true; return Task.FromResult(Array.Empty<byte>()); },
            () => { killed.TrySetResult(); return Task.CompletedTask; },
            MxcEvaluationRunner.CaptureOperationLimit);
        try
        {
            await killed.Task.WaitAsync(MxcEvaluationRunner.FreezeOperationLimit + TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<TimeoutException>(() => run);
            Assert.False(captured);
            Assert.True(clock.Elapsed < MxcEvaluationRunner.FreezeOperationLimit + TimeSpan.FromSeconds(2));
        }
        finally { stalled.TrySetResult(); try { await run; } catch { } }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GivenStalledGate_WhenEvaluationDeadlineOrCancellationOccurs_StopStartsPromptly(bool timedOut)
    {
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new MxcEvaluationEvidence();
        Task<MountlessWaitResult> run = MxcEvaluationRunner.ReleaseAndWaitAsync(
            () => release.Task, Task.Delay(Timeout.InfiniteTimeSpan),
            () => Task.FromResult("observed"),
            TimeSpan.FromMilliseconds(timedOut ? 100 : 30_000), cancellation.Token,
            _ => started.TrySetResult(), _ => { },
            () => { stopped.TrySetResult(); return Task.CompletedTask; }, evidence);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (!timedOut) cancellation.Cancel();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True((await run).GateInterrupted);
            Assert.Equal(timedOut ? EvaluationOutcome.TimedOut : EvaluationOutcome.Cancelled, evidence.OutcomeBeforeCapture);
            Assert.Equal(EvaluationOutcome.InfrastructureFailure, evidence.Outcome);
            Assert.NotNull(evidence.CaptureError);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task GivenSlowGate_WhenResultStalls_TheOriginalDeadlineStillApplies()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new MxcEvaluationEvidence();
        Task<MountlessWaitResult> run = MxcEvaluationRunner.ReleaseAndWaitAsync(
            () => release.Task, Task.Delay(Timeout.InfiniteTimeSpan),
            () => Task.FromResult("observed"), TimeSpan.FromMilliseconds(750), CancellationToken.None,
            _ => { }, _ => { }, () => Task.CompletedTask, evidence);
        await Task.Delay(400);
        release.TrySetResult();
        MountlessWaitResult result = await run.WaitAsync(TimeSpan.FromMilliseconds(600));
        Assert.False(result.GateInterrupted);
        Assert.Equal(EvaluationOutcome.TimedOut, evidence.Outcome);
    }

    [Fact]
    public async Task GivenCancellationBeforeGate_TheGateIsNotReleasedAndStopStarts()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool released = false, killed = false;
        var evidence = new MxcEvaluationEvidence();
        MountlessWaitResult result = await MxcEvaluationRunner.ReleaseAndWaitAsync(
            () => { released = true; return Task.CompletedTask; }, Task.CompletedTask,
            () => Task.FromResult("observed"), TimeSpan.FromSeconds(10), cancellation.Token,
            _ => { }, _ => { }, () => { killed = true; return Task.CompletedTask; }, evidence);
        Assert.True(result.GateInterrupted);
        Assert.False(released);
        Assert.True(killed);
        Assert.Equal(EvaluationOutcome.Cancelled, evidence.OutcomeBeforeCapture);
        Assert.Equal(EvaluationOutcome.InfrastructureFailure, evidence.Outcome);
    }

    [Fact]
    public async Task GivenPendingObservationOrSession_WhenCleaningUp_CanariesAreRetained()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new List<Task>();
        var evidence = new MxcEvaluationEvidence();
        MountlessWaitResult result = await MxcEvaluationRunner.ReleaseAndWaitAsync(
            () => Task.CompletedTask, Task.CompletedTask,
            async () => { await release.Task; return "observed"; },
            TimeSpan.FromSeconds(1), CancellationToken.None, _ => { }, operations.Add,
            () => Task.CompletedTask, evidence);
        Task<string> observation = Assert.IsAssignableFrom<Task<string>>(result.Observation);
        _ = Assert.Single(operations);
        Assert.False(MxcEvaluationRunner.CanRemoveCanaries(true, true, true, operations));
        Assert.False(MxcEvaluationRunner.CanRemoveCanaries(false, true, true, []));
        Assert.False(MxcEvaluationRunner.CanRemoveCanaries(true, false, true, []));
        Assert.False(MxcEvaluationRunner.CanRemoveCanaries(true, true, false, []));
        release.TrySetResult();
        await observation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(MxcEvaluationRunner.CanRemoveCanaries(true, true, true, operations));
    }

    [Fact]
    public async Task GivenExitRace_WhenKillFails_CompletedWaitIsRequired()
    {
        Action fail = () => throw new MxcException(ErrorCode.BackendUnavailable, "already exited");
        await MxcEvaluationRunner.StartKillAsync(fail, Task.CompletedTask);
        await Assert.ThrowsAsync<MxcException>(() =>
            MxcEvaluationRunner.StartKillAsync(fail, Task.Delay(Timeout.InfiniteTimeSpan)));
    }

    [Fact]
    public async Task GivenPauseIsNotVerified_WhenFreezing_CaptureIsNotAttemptedAndKillStarts()
    {
        bool captured = false, killed = false;
        await Assert.ThrowsAsync<IOException>(() => MxcEvaluationRunner.FreezeAndCaptureAsync(
            () => Task.CompletedTask,
            () => Task.FromResult(false),
            () => { captured = true; return Task.FromResult(Array.Empty<byte>()); },
            () => { killed = true; return Task.CompletedTask; },
            TimeSpan.FromSeconds(1)));
        Assert.False(captured);
        Assert.True(killed);
    }

    [Fact]
    public void GivenUntrustedArchive_WhenValidated_UnsafePathsAndOversizedDataAreRejected()
    {
        MxcEvaluationRunner.ValidateArchive(MakeArchive("printf 42"));
        Assert.Throws<IOException>(() => MxcEvaluationRunner.ValidateArchive(MakeArchive("42", "workspace/../escape")));
        Assert.Throws<IOException>(() => MxcEvaluationRunner.ValidateArchive(MakeArchive("42", "/absolute")));
        Assert.Throws<IOException>(() => MxcEvaluationRunner.ValidateArchive(MakeArchive("42", "workspace/sample.sh")));
        Assert.Throws<IOException>(() => MxcEvaluationRunner.ValidateArchive(MakeArchive(new string('x', 1024 * 1024))));
    }

    private static byte[] MakeArchive(string content, string? extraName = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "workspace/"));
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "workspace/sample.sh") { DataStream = data });
            if (extraName is not null)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, extraName));
        }
        return buffer.ToArray();
    }

    [Fact]
    public async Task GivenNoTelemetryApproval_WhenEvaluated_NativeInitializationIsBlocked()
    {
        var result = await _sut.RunAsync(EvaluationSample.Correct, false);
        using var evidence = await ReadEvidenceAsync(result);
        Assert.Equal(EvaluationOutcome.Blocked, result.Outcome);
        Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("nativeVersion").ValueKind);
        Assert.False(evidence.RootElement.GetProperty("checkStarted").GetBoolean());
        Assert.Null(result.ProjectArchivePath);
    }

    [Fact]
    public async Task GivenCorrectChange_WhenEvaluated_ProjectAndEvidenceSurviveAndUnrelatedContainerRemainsUsable()
    {
        RequireTelemetryApproval();
        string storage = Path.Combine(MxcImage.DirectoryPath, $"control-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);
        using var session = new Session(new SessionSettings($"CompareModels-MxcUnrelated-{Guid.NewGuid():N}", storage)
        {
            CpuCount = 1,
            MemorySizeInMB = 512
        });
        bool started = false;
        try
        {
            session.Start();
            started = true;
            await WslFixtureRunner.PullImageAsync(session, CancellationToken.None);
            using var control = CreateContainer(session, ContainerNetworkingMode.None, ["/bin/sleep", "600"]);
            try
            {
                control.Start();
                var result = await _sut.RunAsync(EvaluationSample.Correct, true);
                using var document = await ReadEvidenceAsync(result);
                var root = document.RootElement;
                Assert.True(result.Outcome == EvaluationOutcome.Passed, result.Summary);
                Assert.True(root.GetProperty("boundaryVerified").GetBoolean());
                Assert.True(root.GetProperty("canaryControlVerified").GetBoolean());
                Assert.True(root.GetProperty("sessionAbsentAfterCleanup").GetBoolean());
                Assert.True(root.GetProperty("noNewDaemonObserved").GetBoolean());
                Assert.Equal("false", root.GetProperty("effectivePrivileged").GetString());
                Assert.Equal("none", root.GetProperty("containerInspect").GetProperty("HostConfig").GetProperty("NetworkMode").GetString());
                Assert.Equal($"sha256:{MxcImage.ConfigSha256}", root.GetProperty("imageId").GetString());
                foreach (string marker in new[]
                {
                    "NETWORK_NONE_VERIFIED", "HOST_AND_CREDENTIAL_CANARIES_DENIED",
                    "PRIVILEGE_DROP_VERIFIED",
                    "WORKSPACE_WRITE_OK", "WORKSPACE_SYMLINK_WRITE_OK", "WORKSPACE_RENAME_OK",
                    "DIRECT_WRITE_DENIED", "SYMLINK_WRITE_DENIED", "CHECK_RENAME_DENIED", "MXC_INTEGRITY_AFTER"
                })
                    Assert.Contains(marker, root.GetProperty("standardOutput").GetString());
                Assert.Equal(0, root.GetProperty("checkExitCode").GetInt32());
                Assert.True(root.GetProperty("evaluationElapsedMilliseconds").GetDouble() >= 0);
                Assert.Empty(root.GetProperty("cleanupErrors").EnumerateArray());
                Assert.Empty(root.GetProperty("persistenceErrors").EnumerateArray());
                Assert.Empty(root.GetProperty("containerInspect").GetProperty("Mounts").EnumerateArray());
                Assert.True(root.GetProperty("pauseVerified").GetBoolean());
                Assert.Contains("42", ReadProjectFile(result, "sample.sh"));
                Assert.Equal(ContainerState.Running, control.State);
                var usable = await WslFixtureRunner.RunProcessAsync(control,
                    ["/bin/sh", "-c", "printf 'CONTROL_OK\\n'"],
                    TimeSpan.FromSeconds(10), CancellationToken.None, requiredMarker: "CONTROL_OK");
                Assert.Equal(0, usable.ExitCode);
            }
            finally { StopAndDeleteContainer(control); }
        }
        finally
        {
            if (started) session.Terminate();
            Directory.Delete(storage, recursive: true);
        }
    }

    [Theory]
    [InlineData(EvaluationSample.Incorrect)]
    [InlineData(EvaluationSample.Tampered)]
    public async Task GivenIncorrectOrTamperedChange_WhenEvaluated_TrustedCheckFails(EvaluationSample sample)
    {
        RequireTelemetryApproval();
        var result = await _sut.RunAsync(sample, true);
        using var evidence = await ReadEvidenceAsync(result);
        var root = evidence.RootElement;
        Assert.True(result.Outcome == EvaluationOutcome.Failed, result.Summary);
        Assert.True(root.GetProperty("checkStarted").GetBoolean());
        Assert.Equal(1, root.GetProperty("checkExitCode").GetInt32());
        Assert.Contains("MXC_INTEGRITY_AFTER", root.GetProperty("standardOutput").GetString());
        Assert.Contains("41", ReadProjectFile(result, "sample.sh"));
        if (sample == EvaluationSample.Tampered)
        {
            foreach (string marker in new[]
            {
                "DIRECT_WRITE_DENIED", "SYMLINK_WRITE_DENIED", "CHECK_RENAME_DENIED",
                "PARENT_FD_WRITE_DENIED", "CHECK_PASSED"
            })
                Assert.Contains(marker, root.GetProperty("standardError").GetString());
            Assert.Equal("#!/bin/sh\nexit 0\n", ReadProjectFile(result, Path.Combine("tests", "check.sh")));
            Assert.Equal("CHECK_PASSED\n", ReadProjectFile(result, "report.txt"));
            Assert.Equal(TarEntryType.SymbolicLink, ReadProjectEntryType(result, "check-link"));
        }
        Assert.True(root.GetProperty("sessionAbsentAfterCleanup").GetBoolean());
        Assert.Empty(root.GetProperty("cleanupErrors").EnumerateArray());
    }

    [Fact]
    public async Task GivenMissingCommand_WhenEvaluated_CheckIsUnrunnableNotFailed()
    {
        RequireTelemetryApproval();
        var result = await _sut.RunForTestAsync(new(EvaluationSample.Incorrect, MissingCheck: true), true);
        using var evidence = await ReadEvidenceAsync(result);
        Assert.True(result.Outcome == EvaluationOutcome.Unrunnable, result.Summary);
        Assert.False(evidence.RootElement.GetProperty("checkStarted").GetBoolean());
        Assert.Contains("41", ReadProjectFile(result, "sample.sh"));
    }

    [Fact]
    public async Task GivenSubjectExit126_WhenEvaluated_ExecutedCheckIsFailedNotUnrunnable()
    {
        RequireTelemetryApproval();
        var result = await _sut.RunForTestAsync(new(EvaluationSample.Incorrect, SubjectExit126: true), true);
        using var evidence = await ReadEvidenceAsync(result);
        Assert.True(result.Outcome == EvaluationOutcome.Failed, result.Summary);
        Assert.True(evidence.RootElement.GetProperty("checkStarted").GetBoolean());
        Assert.Contains("subject_exit=126", evidence.RootElement.GetProperty("standardOutput").GetString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GivenWeakenedProtection_WhenEvaluated_SampleIsBlocked(bool writableCheck, bool incompleteDrop)
    {
        RequireTelemetryApproval();
        var result = await _sut.RunForTestAsync(new(EvaluationSample.Correct,
            WeakenCheckPermissions: writableCheck, OmitBoundingDrop: incompleteDrop), true);
        using var evidence = await ReadEvidenceAsync(result);
        Assert.True(result.Outcome == EvaluationOutcome.Blocked, result.Summary);
        Assert.False(evidence.RootElement.GetProperty("checkStarted").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("boundaryVerified").GetBoolean());
    }

    [Fact]
    public async Task GivenUnverifiedFinalIntegrity_WhenEvaluated_PassingCheckIsBlocked()
    {
        RequireTelemetryApproval();
        var result = await _sut.RunForTestAsync(new(EvaluationSample.Correct, OmitFinalIntegrity: true), true);
        using var evidence = await ReadEvidenceAsync(result);
        Assert.True(result.Outcome == EvaluationOutcome.Blocked, result.Summary);
        Assert.True(evidence.RootElement.GetProperty("checkStarted").GetBoolean());
        Assert.Equal(0, evidence.RootElement.GetProperty("checkExitCode").GetInt32());
    }

    [Fact]
    public async Task GivenRetentionFailure_WhenEvaluated_ItIsInfrastructureFailureNotPassed()
    {
        RequireTelemetryApproval();
        var result = await _sut.RunForTestAsync(new(EvaluationSample.Correct, FailCapture: true), true);
        using var evidence = await ReadEvidenceAsync(result);
        Assert.True(result.Outcome == EvaluationOutcome.InfrastructureFailure, result.Summary);
        Assert.Equal("Passed", evidence.RootElement.GetProperty("outcomeBeforeCapture").GetString());
        Assert.True(evidence.RootElement.GetProperty("pauseVerified").GetBoolean());
        Assert.NotNull(evidence.RootElement.GetProperty("captureError").GetString());
        Assert.Null(result.ProjectArchivePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenRunningDescendant_WhenCancelledOrTimedOut_ItStopsAndProjectRemains(bool timedOut)
    {
        RequireTelemetryApproval();
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new StringBuilder();
        var gate = new Lock();
        var options = new MxcEvaluationOptions(EvaluationSample.Correct, LongRunning: true,
            Limit: TimeSpan.FromSeconds(timedOut ? 5 : 30), Output: text =>
            {
                using (gate.EnterScope())
                {
                    received.Append(text);
                    if (received.ToString().Contains("CHILD_READY", StringComparison.Ordinal)
                        && received.ToString().Contains("HEARTBEAT", StringComparison.Ordinal))
                        ready.TrySetResult();
                }
            });
        var run = _sut.RunForTestAsync(options, true, cancellation.Token);
        try
        {
            Task first = await Task.WhenAny(ready.Task, run).WaitAsync(TimeSpan.FromMinutes(3));
            Assert.True(first == ready.Task, first == run ? (await run).Summary : "The descendant did not start.");
            if (!timedOut) cancellation.Cancel();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(90));
            using var evidence = await ReadEvidenceAsync(result);
            var root = evidence.RootElement;
            Assert.True(result.Outcome == (timedOut ? EvaluationOutcome.TimedOut : EvaluationOutcome.Cancelled), result.Summary);
            Assert.Contains("while", root.GetProperty("descendantBeforeStop").GetString());
            Assert.True(root.GetProperty("heartbeatStopped").GetBoolean());
            Assert.True(root.GetProperty("sessionAbsentAfterCleanup").GetBoolean());
            Assert.Empty(root.GetProperty("cleanupErrors").EnumerateArray());
            Assert.Contains("CHILD_READY", ReadProjectFile(result, "sample.sh"));
        }
        finally
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(90));
        }
    }

    private static void RequireTelemetryApproval()
    {
        if (Environment.GetEnvironmentVariable("MXC_SPIKE_ACCEPT_REQUIRED_TELEMETRY") != "1")
            throw new InvalidOperationException(
                "Read the MXC telemetry disclosure and explicitly set MXC_SPIKE_ACCEPT_REQUIRED_TELEMETRY=1 before running integration tests.");
    }

    private static async Task<JsonDocument> ReadEvidenceAsync(MxcEvaluationResult result)
    {
        Assert.True(File.Exists(result.EvidencePath), result.Summary);
        return JsonDocument.Parse(await File.ReadAllTextAsync(result.EvidencePath));
    }

    private static string ReadProjectFile(MxcEvaluationResult result, string relativePath)
    {
        Assert.NotNull(result.ProjectArchivePath);
        using var stream = File.OpenRead(result.ProjectArchivePath);
        using var reader = new TarReader(stream);
        TarEntry? entry;
        string target = "workspace/" + relativePath.Replace('\\', '/');
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.Name == target)
            {
                Assert.NotNull(entry.DataStream);
                using var text = new StreamReader(entry.DataStream);
                return text.ReadToEnd();
            }
        }
        throw new IOException($"Archive entry not found: {target}");
    }

    private static TarEntryType ReadProjectEntryType(MxcEvaluationResult result, string relativePath)
    {
        Assert.NotNull(result.ProjectArchivePath);
        using var stream = File.OpenRead(result.ProjectArchivePath);
        using var reader = new TarReader(stream);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
            if (entry.Name == "workspace/" + relativePath.Replace('\\', '/')) return entry.EntryType;
        throw new IOException($"Archive entry not found: {relativePath}");
    }
}
