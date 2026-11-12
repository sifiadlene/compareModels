using System.Text;
using System.Text.Json;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;
using Xunit;
using static WslContainerFeasibility.ContainerOperations;

namespace WslContainerFeasibility.Tests;

public sealed class WslFixtureRunnerTests
{
    private readonly WslFixtureRunner _sut = new();

    [Fact]
    public async Task GivenIsolatedContainer_WhenFixtureRuns_EvidenceIsSavedAndOtherContainerSurvives()
    {
        using var owner = new TestSession();
        await owner.StartAsync();
        using var control = CreateContainer(
            owner.Session,
            ContainerNetworkingMode.None,
            ["/bin/sleep", "600"]);
        try
        {
            control.Start();
            Assert.Equal(ContainerState.Running, control.State);

            var result = await _sut.RunAsync();
            using var evidence = await ReadEvidenceAsync(result);
            var root = evidence.RootElement;
            Assert.Equal(FixtureOutcome.Completed, result.Outcome);
            Assert.Equal("Completed", root.GetProperty("outcome").GetString());
            Assert.Equal("Passed", root.GetProperty("prerequisites").GetString());
            Assert.Equal("Passed", root.GetProperty("containerConfiguration").GetString());
            Assert.Equal("Passed", root.GetProperty("hostFileCanary").GetString());
            Assert.Equal("Passed", root.GetProperty("hostFileControl").GetString());
            Assert.Equal("Passed", root.GetProperty("credentialFileCanary").GetString());
            Assert.Equal("Passed", root.GetProperty("credentialFileControl").GetString());
            Assert.Equal("Passed", root.GetProperty("credentialEnvironmentCanary").GetString());
            Assert.Equal("Passed", root.GetProperty("networkControl").GetString());
            Assert.Equal("Passed", root.GetProperty("blockedNetwork").GetString());
            Assert.True(root.GetProperty("fixtureStarted").GetBoolean());
            Assert.Equal(0, root.GetProperty("fixtureExitCode").GetInt32());
            Assert.Contains("RESULT=42", root.GetProperty("fixtureOutput").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("windows").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("wslVersion").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("wslContainerService").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("linuxKernel").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("imageId").GetString()));
            Assert.Equal(ImageReference, root.GetProperty("imageReference").GetString());
            Assert.Empty(root.GetProperty("cleanupErrors").EnumerateArray());

            Assert.Equal(ContainerState.Running, control.State);
            var probe = await WslFixtureRunner.RunProcessAsync(
                control,
                ["/bin/sh", "-c", "printf 'CONTROL_OK\\n'"],
                TimeSpan.FromSeconds(10),
                CancellationToken.None,
                requiredMarker: "CONTROL_OK");
            Assert.Equal(0, probe.ExitCode);
        }
        finally
        {
            StopAndDeleteContainer(control);
        }
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("configuration")]
    [InlineData("mount")]
    [InlineData("network")]
    [InlineData("unverified")]
    public async Task GivenFailedBoundary_WhenFixtureIsRequested_ExecutionIsBlocked(string boundary)
    {
        var options = boundary switch
        {
            "credential" => new RunnerTestOptions(InjectCredentialCanary: true),
            "configuration" => new RunnerTestOptions(UseBridgedTaskNetwork: true),
            "mount" => new RunnerTestOptions(MountHostCanaries: true),
            "network" => new RunnerTestOptions(UseControlForBlockedProbe: true),
            "unverified" => new RunnerTestOptions(UnavailableCanaryTool: true),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
        var result = await _sut.RunForTestAsync(options);
        using var evidence = await ReadEvidenceAsync(result);
        var root = evidence.RootElement;

        Assert.Equal(FixtureOutcome.Blocked, result.Outcome);
        Assert.False(root.GetProperty("fixtureStarted").GetBoolean());
        Assert.Empty(root.GetProperty("cleanupErrors").EnumerateArray());
        string failedCheck = boundary switch
        {
            "credential" => "credentialEnvironmentCanary",
            "configuration" => "containerConfiguration",
            "mount" => "containerConfiguration",
            "network" => "blockedNetwork",
            "unverified" => "hostFileCanary",
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
        Assert.Equal(boundary == "unverified" ? "NotRun" : "Failed", root.GetProperty(failedCheck).GetString());
        if (boundary == "unverified")
        {
            Assert.Equal("NotRun", root.GetProperty("credentialFileCanary").GetString());
            Assert.Equal("NotRun", root.GetProperty("credentialEnvironmentCanary").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenRunningFixture_WhenCancelledOrTimedOut_OutcomeIsRecorded(bool timedOut)
    {
        using var cancellation = new CancellationTokenSource();
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new StringBuilder();
        var outputLock = new Lock();
        var options = new RunnerTestOptions(
            LongRunningFixture: true,
            FixtureTimeout: timedOut ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(30),
            FixtureOutput: text =>
            {
                using (outputLock.EnterScope())
                {
                    observed.Append(text);
                    string output = observed.ToString();
                    if (output.Contains("CHILD_READY", StringComparison.Ordinal)
                        && output.Contains("HEARTBEAT", StringComparison.Ordinal))
                    {
                        childStarted.TrySetResult();
                    }
                }
            });
        Task<FixtureRunResult> run = _sut.RunForTestAsync(options, cancellationToken: cancellation.Token);
        try
        {
            Task first = await Task.WhenAny(childStarted.Task, run).WaitAsync(TimeSpan.FromMinutes(2));
            if (first == run)
            {
                var earlyResult = await run;
                Assert.Fail($"The fixture ended before its child started: {earlyResult.Summary}");
            }

            if (!timedOut)
            {
                cancellation.Cancel();
            }

            var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
            using var evidence = await ReadEvidenceAsync(result);
            Assert.Equal(timedOut ? FixtureOutcome.TimedOut : FixtureOutcome.Cancelled, result.Outcome);
            Assert.True(evidence.RootElement.GetProperty("fixtureStarted").GetBoolean());
            Assert.Empty(evidence.RootElement.GetProperty("cleanupErrors").EnumerateArray());
        }
        finally
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenWritingDescendant_WhenCancelledOrTimedOut_HeartbeatStops(bool timedOut)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"compareModels-test-child-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string heartbeat = Path.Combine(directory, "heartbeat");
        using var cancellation = new CancellationTokenSource();
        using var owner = new TestSession();
        try
        {
            await owner.StartAsync();
            using var container = CreateContainer(
                owner.Session,
                ContainerNetworkingMode.None,
                ["/bin/sleep", "600"],
                [new ContainerVolume(directory, "/probe", false)]);
            try
            {
                container.Start();
                Task<CommandResult> process = WslFixtureRunner.RunProcessAsync(
                    container,
                    [
                        "/bin/sh",
                        "-c",
                        """
                        /bin/sh -c 'while :; do printf x >> /probe/heartbeat; /bin/sleep 0.1; done' &
                        child=$!
                        printf '%s\n' "$child" > /probe/child.pid
                        wait "$child"
                        """
                    ],
                    timedOut ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30),
                    cancellation.Token);
                try
                {
                    await WaitForHeartbeatAsync(heartbeat);
                    Assert.True(File.Exists(Path.Combine(directory, "child.pid")));
                    if (timedOut)
                    {
                        await Assert.ThrowsAsync<TimeoutException>(() => process);
                    }
                    else
                    {
                        cancellation.Cancel();
                        await Assert.ThrowsAsync<OperationCanceledException>(() => process);
                    }

                    Assert.Equal(ContainerState.Exited, container.State);
                    long afterStop = new FileInfo(heartbeat).Length;
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    Assert.Equal(afterStop, new FileInfo(heartbeat).Length);
                }
                finally
                {
                    cancellation.Cancel();
                    if (!process.IsCompleted)
                    {
                        try
                        {
                            await process.WaitAsync(TimeSpan.FromSeconds(15));
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                        {
                        }
                    }
                }
            }
            finally
            {
                StopAndDeleteContainer(container);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GivenCancelledToken_WhenProcessWouldStart_ContainerIsStopped()
    {
        using var owner = new TestSession();
        await owner.StartAsync();
        using var container = CreateContainer(
            owner.Session,
            ContainerNetworkingMode.None,
            ["/bin/sleep", "600"]);
        try
        {
            container.Start();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => WslFixtureRunner.RunProcessAsync(
                container,
                ["/bin/sh", "-c", "exit 0"],
                TimeSpan.FromSeconds(10),
                cancellation.Token));
            Assert.Equal(ContainerState.Exited, container.State);
        }
        finally
        {
            StopAndDeleteContainer(container);
        }
    }

    private static async Task<JsonDocument> ReadEvidenceAsync(FixtureRunResult result)
    {
        Assert.True(File.Exists(result.EvidencePath), $"Evidence was not saved: {result.EvidencePath}");
        return JsonDocument.Parse(await File.ReadAllTextAsync(result.EvidencePath));
    }

    private static async Task WaitForHeartbeatAsync(string heartbeat)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(heartbeat) && new FileInfo(heartbeat).Length >= 5)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The synthetic descendant heartbeat did not start.");
    }

    private sealed class TestSession : IDisposable
    {
        private readonly string _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "compareModels",
            "Feasibility",
            "test-controls",
            Guid.NewGuid().ToString("N"));
        private bool _started;

        internal TestSession()
        {
            Directory.CreateDirectory(_storagePath);
            Session = new Session(new SessionSettings($"CompareModels-Test-{Guid.NewGuid():N}", _storagePath)
            {
                CpuCount = 1,
                MemorySizeInMB = 512
            });
        }

        internal Session Session { get; }

        internal async Task StartAsync()
        {
            Session.Start();
            _started = true;
            await Session.PullImageAsync(new PullImageOptions(ImageReference));
        }

        public void Dispose()
        {
            bool terminated = !_started;
            try
            {
                if (_started)
                {
                    Session.Terminate();
                    terminated = true;
                }
            }
            finally
            {
                Session.Dispose();
                if (terminated)
                {
                    Directory.Delete(_storagePath, recursive: true);
                }
            }
        }
    }
}
