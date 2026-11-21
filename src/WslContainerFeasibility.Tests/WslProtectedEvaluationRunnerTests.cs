using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;
using Xunit;
using static WslContainerFeasibility.ContainerOperations;

namespace WslContainerFeasibility.Tests;

public sealed class WslProtectedEvaluationRunnerTests
{
    private readonly WslProtectedEvaluationRunner _sut = new();

    [Fact]
    public async Task GivenCorrectSample_WhenEvaluated_EvidenceAndProjectRemainAndOtherContainerSurvives()
    {
        string storage = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "compareModels", "Feasibility", "test-controls", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        using var session = new Session(new SessionSettings($"CompareModels-EvaluationControl-{Guid.NewGuid():N}", storage)
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
                var result = await _sut.RunAsync(EvaluationSample.Correct);
                using var evidence = await ReadEvidenceAsync(result);
                var root = evidence.RootElement;
                Assert.True(result.Outcome == EvaluationOutcome.Passed, $"{result.Summary}; {result.EvidencePath}");
                Assert.Equal("Passed", root.GetProperty("outcome").GetString());
                Assert.Equal("Passed", root.GetProperty("checkIntegrityBefore").GetString());
                Assert.Equal("Passed", root.GetProperty("checkIntegrityAfter").GetString());
                Assert.Equal("false", root.GetProperty("effectivePrivileged").GetString());
                Assert.Equal(11, root.GetProperty("privilegeNegativeControlExitCode").GetInt32());
                Assert.Equal(0, root.GetProperty("privilegeProbeExitCode").GetInt32());
                Assert.Equal(0, root.GetProperty("protectionProbeExitCode").GetInt32());
                string protectionOutput = root.GetProperty("protectionProbeOutput").GetString()!;
                foreach (string marker in new[]
                {
                    "WORKSPACE_WRITE_OK", "WORKSPACE_SYMLINK_WRITE_OK", "WORKSPACE_RENAME_OK",
                    "DIRECT_WRITE_DENIED", "SYMLINK_WRITE_DENIED", "CHECK_RENAME_DENIED"
                })
                {
                    Assert.Contains(marker, protectionOutput);
                }
                Assert.True(root.GetProperty("checkStarted").GetBoolean());
                Assert.True(root.GetProperty("checkSetupReady").GetBoolean());
                Assert.Equal(0, root.GetProperty("checkPreflightExitCode").GetInt32());
                Assert.Equal(0, root.GetProperty("checkExitCode").GetInt32());
                Assert.Contains("CHECK_PASSED", root.GetProperty("checkStandardOutput").GetString());
                Assert.True(root.GetProperty("evaluationLimitMilliseconds").GetDouble() > 0);
                Assert.True(root.GetProperty("evaluationElapsedMilliseconds").GetDouble() >= 0);
                var boundaries = root.GetProperty("boundaries");
                Assert.False(boundaries.TryGetProperty("outcome", out _));
                foreach (string field in new[]
                {
                    "prerequisites", "containerConfiguration", "hostFileCanary", "hostFileControl",
                    "credentialFileCanary", "credentialFileControl", "credentialEnvironmentCanary",
                    "networkControl", "blockedNetwork"
                })
                {
                    Assert.Equal("Passed", boundaries.GetProperty(field).GetString());
                }
                Assert.Contains("interfaces=lo", boundaries.GetProperty("blockedNetworkOutput").GetString());
                Assert.Equal(WslProtectedEvaluationRunner.EvaluationImage, boundaries.GetProperty("imageReference").GetString());
                Assert.False(string.IsNullOrWhiteSpace(boundaries.GetProperty("imageId").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(boundaries.GetProperty("wslVersion").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(boundaries.GetProperty("linuxKernel").GetString()));
                Assert.Empty(root.GetProperty("cleanupErrors").EnumerateArray());
                Assert.Empty(root.GetProperty("persistenceErrors").EnumerateArray());
                Assert.Equal("Complete", root.GetProperty("cleanupState").GetString());
                Assert.Contains("printf '42", ReadProjectFile(result, "workspace/sample.sh"));

                Assert.Equal(ContainerState.Running, control.State);
                var probe = await WslFixtureRunner.RunProcessAsync(
                    control, ["/bin/sh", "-c", "printf 'CONTROL_OK\\n'"],
                    TimeSpan.FromSeconds(10), CancellationToken.None, requiredMarker: "CONTROL_OK");
                Assert.Equal(0, probe.ExitCode);
            }
            finally
            {
                StopAndDeleteContainer(control);
            }
        }
        finally
        {
            if (started)
            {
                session.Terminate();
            }
            Directory.Delete(storage, recursive: true);
        }
    }

    [Theory]
    [InlineData(EvaluationSample.Incorrect)]
    [InlineData(EvaluationSample.Tampered)]
    public async Task GivenIncorrectOrTamperedSample_WhenEvaluated_TrustedCheckFails(EvaluationSample sample)
    {
        var result = await _sut.RunAsync(sample);
        using var evidence = await ReadEvidenceAsync(result);
        var root = evidence.RootElement;
        Assert.True(result.Outcome == EvaluationOutcome.Failed, $"{result.Summary}; {result.EvidencePath}");
        Assert.True(root.GetProperty("checkStarted").GetBoolean());
        Assert.Equal(1, root.GetProperty("checkExitCode").GetInt32());
        Assert.Contains("CHECK_FAILED", root.GetProperty("checkStandardOutput").GetString());
        Assert.Equal("Passed", root.GetProperty("checkIntegrityAfter").GetString());
        Assert.Empty(root.GetProperty("cleanupErrors").EnumerateArray());
        Assert.Contains("41", ReadProjectFile(result, "workspace/sample.sh"));
        if (sample == EvaluationSample.Tampered)
        {
            string error = root.GetProperty("checkStandardError").GetString()!;
            foreach (string marker in new[]
            {
                "WORKSPACE_WRITE_OK", "DIRECT_WRITE_DENIED", "SYMLINK_WRITE_DENIED",
                "CHECK_RENAME_DENIED", "PARENT_FD_WRITE_DENIED", "CHECK_PASSED"
            })
            {
                Assert.Contains(marker, error);
            }
            Assert.Equal("#!/bin/sh\nexit 0\n", ReadProjectFile(result, "workspace/tests/check.sh"));
            Assert.Equal("CHECK_PASSED\n", ReadProjectFile(result, "workspace/report.txt"));
            Assert.Equal(TarEntryType.SymbolicLink, ReadProjectEntryType(result, "workspace/check-link"));
        }
    }

    [Fact]
    public async Task GivenMissingCheck_WhenEvaluated_ItIsUnrunnableRatherThanFailed()
    {
        var result = await _sut.RunForTestAsync(new(EvaluationSample.Incorrect, MissingCheck: true));
        using var evidence = await ReadEvidenceAsync(result);
        var root = evidence.RootElement;
        Assert.True(result.Outcome == EvaluationOutcome.Unrunnable, $"{result.Summary}; {result.EvidencePath}");
        Assert.False(root.GetProperty("checkStarted").GetBoolean());
        Assert.Equal(11, root.GetProperty("checkPreflightExitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("checkExitCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("evaluationElapsedMilliseconds").ValueKind);
        Assert.Contains("41", ReadProjectFile(result, "workspace/sample.sh"));
    }

    [Fact]
    public async Task GivenSubjectExit126_WhenEvaluated_TheExecutedCheckIsFailedRatherThanUnrunnable()
    {
        var result = await _sut.RunForTestAsync(new(EvaluationSample.Incorrect, SubjectExit126: true));
        using var evidence = await ReadEvidenceAsync(result);
        var root = evidence.RootElement;
        Assert.True(result.Outcome == EvaluationOutcome.Failed, $"{result.Summary}; {result.EvidencePath}");
        Assert.True(root.GetProperty("checkStarted").GetBoolean());
        Assert.Equal(1, root.GetProperty("checkExitCode").GetInt32());
        Assert.Contains("subject_exit=126", root.GetProperty("checkStandardOutput").GetString());
    }

    [Fact]
    public async Task GivenUnverifiedPrivilegeDrop_WhenEvaluated_ExecutionIsBlocked()
    {
        var result = await _sut.RunForTestAsync(new(EvaluationSample.Correct, OmitBoundingDrop: true));
        using var evidence = await ReadEvidenceAsync(result);
        var root = evidence.RootElement;
        Assert.True(result.Outcome == EvaluationOutcome.Blocked, $"{result.Summary}; {result.EvidencePath}");
        Assert.False(root.GetProperty("checkStarted").GetBoolean());
        Assert.NotEqual(0, root.GetProperty("privilegeProbeExitCode").GetInt32());
    }

    [Fact]
    public async Task GivenWritableAuthoritativeCheck_WhenEvaluated_ExecutionIsBlocked()
    {
        var result = await _sut.RunForTestAsync(new(EvaluationSample.Correct, WeakenCheckPermissions: true));
        using var evidence = await ReadEvidenceAsync(result);
        var root = evidence.RootElement;
        Assert.True(result.Outcome == EvaluationOutcome.Blocked, $"{result.Summary}; {result.EvidencePath}");
        Assert.False(root.GetProperty("checkStarted").GetBoolean());
        Assert.Equal(31, root.GetProperty("protectionProbeExitCode").GetInt32());
    }

    [Fact]
    public async Task GivenProjectCaptureFailure_WhenEvaluated_ItIsAnInfrastructureFailure()
    {
        var result = await _sut.RunForTestAsync(new(EvaluationSample.Correct, MissingProjectForCapture: true));
        using var evidence = await ReadEvidenceAsync(result);
        var root = evidence.RootElement;
        Assert.True(result.Outcome == EvaluationOutcome.InfrastructureFailure, $"{result.Summary}; {result.EvidencePath}");
        Assert.True(root.GetProperty("checkStarted").GetBoolean());
        Assert.Equal(0, root.GetProperty("checkExitCode").GetInt32());
        Assert.Equal("Passed", root.GetProperty("evaluationOutcomeBeforeCapture").GetString());
        Assert.Contains("Could not capture", root.GetProperty("captureError").GetString());
        Assert.Null(result.ProjectArchivePath);
        Assert.Empty(root.GetProperty("cleanupErrors").EnumerateArray());
    }

    [Fact]
    public async Task GivenStoppedEvaluator_WhenCheckCannotBeReverified_ExecutionIsBlocked()
    {
        var result = await _sut.RunForTestAsync(new(EvaluationSample.Correct, StopBeforeIntegrityCheck: true));
        using var evidence = await ReadEvidenceAsync(result);
        var root = evidence.RootElement;
        Assert.True(result.Outcome == EvaluationOutcome.Blocked, $"{result.Summary}; {result.EvidencePath}");
        Assert.True(root.GetProperty("checkStarted").GetBoolean());
        Assert.Equal(0, root.GetProperty("checkExitCode").GetInt32());
        Assert.Equal("FailedOrUnverified", root.GetProperty("checkIntegrityAfter").GetString());
        Assert.Contains("42", ReadProjectFile(result, "workspace/sample.sh"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenRunningDescendant_WhenCancelledOrTimedOut_ItStopsAndProjectIsRetained(bool timedOut)
    {
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new StringBuilder();
        var gate = new Lock();
        string? childPid = null;
        string? childStateAfterStop = null;
        var options = new EvaluationTestOptions(
            EvaluationSample.Correct,
            LongRunning: true,
            EvaluationLimit: timedOut ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30),
            Output: text =>
            {
                using (gate.EnterScope())
                {
                    observed.Append(text);
                    if (observed.ToString().Contains("CHILD_READY", StringComparison.Ordinal)
                        && observed.ToString().Contains("HEARTBEAT", StringComparison.Ordinal))
                    {
                        ready.TrySetResult();
                    }
                }
            },
            BeforeStop: async (sessionName, containerId) =>
            {
                var top = await RunDaemonCommandAsync(
                    sessionName, ["docker", "top", containerId, "-eo", "pid,ppid,args"], TimeSpan.FromSeconds(10));
                Assert.Equal(0, top.ExitCode);
                var match = Regex.Match(
                    top.StandardOutput,
                    @"(?m)^\s*(\d+)\s+\d+\s+/bin/sh -c while .*comparemodels-evaluation-child-[0-9a-f]{32}");
                Assert.True(match.Success, $"The descendant was not visible before stopping: {top.StandardOutput}");
                childPid = match.Groups[1].Value;
                return top.StandardOutput;
            },
            AfterStop: async (sessionName, _) =>
            {
                Assert.NotNull(childPid);
                var state = await RunDaemonCommandAsync(
                    sessionName,
                    [
                        "sh", "-c",
                        "if [ -r /proc/$1/status ]; then grep '^State:' /proc/$1/status; else echo PROCESS_ABSENT; fi",
                        "check-descendant", childPid
                    ],
                    TimeSpan.FromSeconds(10));
                Assert.Equal(0, state.ExitCode);
                childStateAfterStop = state.StandardOutput;
                return state.StandardOutput;
            });
        Task<EvaluationRunResult> run = _sut.RunForTestAsync(options, cancellation.Token);
        try
        {
            Task first = await Task.WhenAny(ready.Task, run).WaitAsync(TimeSpan.FromMinutes(3));
            if (first == run)
            {
                var early = await run;
                Assert.Fail($"The evaluator ended before its descendant started: {early.Summary}; {early.EvidencePath}");
            }
            if (!timedOut)
            {
                cancellation.Cancel();
            }

            var result = await run.WaitAsync(TimeSpan.FromSeconds(60));
            using var evidence = await ReadEvidenceAsync(result);
            var root = evidence.RootElement;
            Assert.True(result.Outcome == (timedOut ? EvaluationOutcome.TimedOut : EvaluationOutcome.Cancelled),
                $"{result.Summary}; {result.EvidencePath}");
            Assert.True(root.GetProperty("checkStarted").GetBoolean());
            Assert.NotNull(childPid);
            Assert.True(childStateAfterStop?.Contains("PROCESS_ABSENT", StringComparison.Ordinal) == true
                || Regex.IsMatch(childStateAfterStop ?? "", @"State:\s+Z"), childStateAfterStop);
            Assert.Contains(childPid!, root.GetProperty("descendantBeforeStop").GetString());
            Assert.Equal(childStateAfterStop, root.GetProperty("descendantAfterStop").GetString());
            Assert.Empty(root.GetProperty("cleanupErrors").EnumerateArray());
            Assert.Contains("CHILD_READY", ReadProjectFile(result, "workspace/sample.sh"));
        }
        finally
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    private static async Task<JsonDocument> ReadEvidenceAsync(EvaluationRunResult result)
    {
        Assert.True(File.Exists(result.EvidencePath), $"Evidence was not saved: {result.EvidencePath}");
        return JsonDocument.Parse(await File.ReadAllTextAsync(result.EvidencePath));
    }

    private static string ReadProjectFile(EvaluationRunResult result, string name)
    {
        Assert.NotNull(result.ProjectArchivePath);
        Assert.True(File.Exists(result.ProjectArchivePath), $"Project archive was not saved: {result.ProjectArchivePath}");
        using var archive = File.OpenRead(result.ProjectArchivePath);
        using var reader = new TarReader(archive);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.Name == name)
            {
                Assert.NotNull(entry.DataStream);
                using var content = new StreamReader(entry.DataStream);
                return content.ReadToEnd();
            }
        }
        throw new InvalidOperationException($"Project archive did not contain {name}.");
    }

    private static TarEntryType ReadProjectEntryType(EvaluationRunResult result, string name)
    {
        Assert.NotNull(result.ProjectArchivePath);
        using var archive = File.OpenRead(result.ProjectArchivePath);
        using var reader = new TarReader(archive);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.Name == name)
            {
                return entry.EntryType;
            }
        }
        throw new InvalidOperationException($"Project archive did not contain {name}.");
    }
}
