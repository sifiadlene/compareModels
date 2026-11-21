using System.Text;
using System.Text.Json;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;
using Xunit;
using static WslContainerFeasibility.ContainerOperations;

namespace MxcWslcFeasibility.Tests;

public sealed class MxcEvaluationRunnerTests
{
    private readonly MxcEvaluationRunner _sut = new();

    [Fact]
    public async Task GivenNoTelemetryApproval_WhenEvaluated_NativeInitializationIsBlocked()
    {
        var result = await _sut.RunAsync(EvaluationSample.Correct, false);
        using var evidence = await ReadEvidenceAsync(result);
        Assert.Equal(EvaluationOutcome.Blocked, result.Outcome);
        Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("nativeVersion").ValueKind);
        Assert.False(evidence.RootElement.GetProperty("checkStarted").GetBoolean());
        Assert.Null(result.ProjectDirectoryPath);
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
                    "READONLY_SOURCE_WRITE_DENIED", "PRIVILEGE_DROP_VERIFIED",
                    "WORKSPACE_WRITE_OK", "WORKSPACE_SYMLINK_WRITE_OK", "WORKSPACE_RENAME_OK",
                    "DIRECT_WRITE_DENIED", "SYMLINK_WRITE_DENIED", "CHECK_RENAME_DENIED", "MXC_INTEGRITY_AFTER"
                })
                    Assert.Contains(marker, root.GetProperty("standardOutput").GetString());
                Assert.Equal(0, root.GetProperty("checkExitCode").GetInt32());
                Assert.True(root.GetProperty("evaluationElapsedMilliseconds").GetDouble() >= 0);
                Assert.Empty(root.GetProperty("cleanupErrors").EnumerateArray());
                Assert.Empty(root.GetProperty("persistenceErrors").EnumerateArray());
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
            Assert.True((File.GetAttributes(Path.Combine(result.ProjectDirectoryPath!, "check-link"))
                & FileAttributes.ReparsePoint) != 0);
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
        Assert.NotNull(evidence.RootElement.GetProperty("captureError").GetString());
        Assert.Null(result.ProjectDirectoryPath);
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
        Assert.NotNull(result.ProjectDirectoryPath);
        Assert.True(Directory.Exists(result.ProjectDirectoryPath));
        return File.ReadAllText(Path.Combine(result.ProjectDirectoryPath, relativePath));
    }
}
