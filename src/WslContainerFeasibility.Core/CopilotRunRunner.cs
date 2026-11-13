using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.WSL.Containers;
using static WslContainerFeasibility.ContainerOperations;
using WslContainer = Microsoft.WSL.Containers.Container;
using DotNetProcess = System.Diagnostics.Process;

namespace WslContainerFeasibility;

/// <summary>Runs one approved Copilot attempt against a disposable WSL fixture.</summary>
public sealed class CopilotRunRunner
{
    private const string SdkPackageVersion = "1.0.33";
    private const string CopilotCliRuntimeVersion = "1.0.111";
    private const string ProjectRoot = "/workspace/project";
    private const string SourcePath = $"{ProjectRoot}/src/greeting.sh";
    private const string OriginalSource = "#!/bin/sh\nprintf 'Hello, world!\\n'\n";
    private const string RequestedOutput = "Hello, Copilot!";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Starts the official Copilot CLI login flow for this app's Copilot home.</summary>
    /// <returns>The started login process.</returns>
    public DotNetProcess StartOfficialSignIn()
    {
        string copilotHome = GetCopilotHome();
        Directory.CreateDirectory(copilotHome);
        var startInfo = new ProcessStartInfo("copilot")
        {
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add("login");
        startInfo.Environment["COPILOT_HOME"] = copilotHome;
        return DotNetProcess.Start(startInfo)
            ?? throw new InvalidOperationException("The Copilot CLI login process could not be started.");
    }

    /// <summary>Lists model strategies available to the signed-in Copilot CLI user.</summary>
    public async Task<IReadOnlyList<CopilotModelStrategy>> ListStrategiesAsync(
        CancellationToken cancellationToken = default)
    {
        string copilotHome = GetCopilotHome();
        Directory.CreateDirectory(copilotHome);
        await using var client = CreateClient(copilotHome, copilotHome);
        await client.StartAsync(cancellationToken);
        var models = await client.ListModelsAsync(cancellationToken);
        return models
            .Select(model => new CopilotModelStrategy(model.Id, model.Name))
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Runs one bounded Copilot attempt after explicit user approval.</summary>
    public async Task<CopilotRunResult> RunOnceAsync(
        string strategyId,
        bool userApproved,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        string runId = Guid.NewGuid().ToString("N");
        var evidence = new CopilotRunEvidence
        {
            RunId = runId,
            StartedUtc = DateTimeOffset.UtcNow,
            Windows = Environment.OSVersion.VersionString,
            DotNet = Environment.Version.ToString(),
            WslContainersPackage = WslFixtureRunner.ApiPackageVersion,
            CopilotSdkPackage = SdkPackageVersion,
            CopilotCliRuntime = CopilotCliRuntimeVersion,
            ImageReference = ImageReference,
            RequestedStrategy = strategyId
        };

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new IOException("The local application-data directory is unavailable.");
        }

        string runRoot = Path.Combine(localAppData, "compareModels", "Feasibility", "copilot-runs", runId);
        string wslStorage = Path.Combine(runRoot, "wsl-session");
        string sdkWorkingDirectory = Path.Combine(runRoot, "sdk-working");
        string sdkConfigDirectory = Path.Combine(runRoot, "sdk-config");
        string canaryDirectory = Path.Combine(Path.GetTempPath(), $"compareModels-copilot-canary-{runId}");
        string credentialVariable = $"COMPAREMODELS_COPILOT_CREDENTIAL_{runId.ToUpperInvariant()}";
        bool runRootCreated = false;
        bool canaryCreated = false;
        bool variableSet = false;
        string? previousCredentialValue = null;
        bool sessionStartAttempted = false;
        bool sessionTerminated = false;
        Session? wslSession = null;
        WslContainer? taskContainer = null;
        CopilotClient? client = null;
        CopilotSession? copilotSession = null;
        SemaphoreSlim? sandboxCommandGate = null;
        bool agentInProgress = false;
        var cleanupErrors = new List<string>();
        var capturedEvents = new List<CopilotRunEvent>();
        var reportedModels = new HashSet<string>(StringComparer.Ordinal);
        var eventLock = new Lock();

        try
        {
            if (!userApproved)
            {
                evidence.Outcome = FixtureOutcome.Blocked;
                evidence.Error = "The potentially billable Copilot attempt was not approved.";
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Checking WSL and Copilot prerequisites.");
                var prerequisites = new WslFixtureRunner().CheckPrerequisites();
                evidence.Prerequisites = prerequisites.Ready ? "Passed" : "Failed";
                evidence.PrerequisiteDetails = prerequisites.Details;
                if (!prerequisites.Ready)
                {
                    throw new BoundaryException(prerequisites.Details);
                }

                Directory.CreateDirectory(runRoot);
                runRootCreated = true;
                Directory.CreateDirectory(sdkWorkingDirectory);
                Directory.CreateDirectory(sdkConfigDirectory);
                string copilotHome = GetCopilotHome();
                Directory.CreateDirectory(copilotHome);

                progress?.Report("Connecting to the signed-in Copilot account and checking the selected strategy.");
                client = CreateClient(copilotHome, sdkWorkingDirectory);
                await client.StartAsync(cancellationToken);
                var strategies = await client.ListModelsAsync(cancellationToken);
                if (!strategies.Any(model => string.Equals(model.Id, strategyId, StringComparison.Ordinal)))
                {
                    throw new BoundaryException(
                        $"The selected model strategy '{strategyId}' is no longer available to this account.");
                }

                evidence.WslVersion = await WslFixtureRunner.ReadWslVersionAsync(cancellationToken);
                var serviceVersion = WslcService.GetVersion();
                evidence.WslContainerService = $"{serviceVersion.Major}.{serviceVersion.Minor}.{serviceVersion.Revision}";

                if (Directory.Exists(wslStorage) || Directory.Exists(canaryDirectory)
                    || Environment.GetEnvironmentVariable(credentialVariable) is not null)
                {
                    throw new IOException("A generated Copilot feasibility resource name already exists.");
                }

                Directory.CreateDirectory(canaryDirectory);
                canaryCreated = true;
                string hostCanary = Path.Combine(canaryDirectory, "host-file.txt");
                string credentialCanary = Path.Combine(canaryDirectory, "credential.txt");
                await File.WriteAllTextAsync(hostCanary, "synthetic host-file canary", cancellationToken);
                await File.WriteAllTextAsync(credentialCanary, "synthetic credential canary", cancellationToken);
                previousCredentialValue = Environment.GetEnvironmentVariable(credentialVariable);
                Environment.SetEnvironmentVariable(credentialVariable, "synthetic credential canary");
                variableSet = true;

                progress?.Report("Starting a private WSL session and preparing the isolated task.");
                wslSession = new Session(new SessionSettings($"CompareModels-Copilot-{runId}", wslStorage)
                {
                    CpuCount = 1,
                    MemorySizeInMB = 512,
                    HostLoopback = "host.wslc.internal"
                });
                sessionStartAttempted = true;
                wslSession.Start();
                await WslFixtureRunner.PullImageAsync(wslSession, cancellationToken);
                taskContainer = CreateContainer(
                    wslSession,
                    ContainerNetworkingMode.None,
                    ["/bin/sleep", "600"]);
                taskContainer.Start();
                if (taskContainer.State != ContainerState.Running)
                {
                    throw new BoundaryException("The Copilot task container did not start.");
                }

                var boundaryEvidence = new FixtureEvidence
                {
                    RunId = runId,
                    ImageReference = ImageReference,
                    WslVersion = evidence.WslVersion,
                    WslContainerService = evidence.WslContainerService
                };
                await WslFixtureRunner.VerifyBoundariesAsync(
                    wslSession,
                    taskContainer,
                    hostCanary,
                    credentialCanary,
                    credentialVariable,
                    new RunnerTestOptions(),
                    boundaryEvidence,
                    cancellationToken);
                evidence.BoundaryChecks = new CopilotBoundaryChecks(
                    boundaryEvidence.ContainerConfiguration,
                    boundaryEvidence.HostFileCanary,
                    boundaryEvidence.CredentialFileCanary,
                    boundaryEvidence.CredentialEnvironmentCanary,
                    boundaryEvidence.NetworkControl,
                    boundaryEvidence.BlockedNetwork,
                    boundaryEvidence.ImageId);
                evidence.BoundaryVerified = true;

                Environment.SetEnvironmentVariable(credentialVariable, previousCredentialValue);
                variableSet = false;
                CaptureCleanupError(cleanupErrors, "synthetic canary directory",
                    () => Directory.Delete(canaryDirectory, recursive: true));
                canaryCreated = false;

                await WriteFixtureAsync(taskContainer, cancellationToken);
                sandboxCommandGate = new SemaphoreSlim(1, 1);
                var tool = CreateSandboxTool(taskContainer, sandboxCommandGate, progress, cancellationToken);

                progress?.Report("Starting the approved, ten-minute Copilot attempt.");
                copilotSession = await client.CreateSessionAsync(new SessionConfig
                {
                    SessionId = $"compare-models-{runId}",
                    Model = strategyId,
                    Tools = [tool],
                    AvailableTools = ["custom:run_in_sandbox"],
                    WorkingDirectory = sdkWorkingDirectory,
                    ConfigDirectory = sdkConfigDirectory,
                    SkipCustomInstructions = true,
                    IncludedBuiltinSkills = [],
                    CustomAgents = [],
                    McpServers = new Dictionary<string, McpServerConfig>(),
                    Memory = new MemoryConfiguration { Enabled = false },
                    EnableSessionStore = false,
                    OnPermissionRequest = PermissionHandler.ApproveAll
                });
                using var subscription = copilotSession.On<SessionEvent>(evt =>
                {
                    lock (eventLock)
                    {
                        capturedEvents.Add(new(
                            DateTimeOffset.UtcNow,
                            evt.Type,
                            JsonSerializer.Serialize(evt, evt.GetType())));
                        if (evt is AssistantMessageEvent message
                            && !string.IsNullOrWhiteSpace(message.Data.Model))
                        {
                            reportedModels.Add(message.Data.Model);
                        }
                    }

                    progress?.Report($"Copilot event: {evt.Type}");
                });

                evidence.AgentStarted = true;
                agentInProgress = true;
                var finalMessage = await copilotSession.SendAndWaitAsync(
                    """
                    Edit the controlled repository at /workspace/project. Update src/greeting.sh so that running it prints exactly "Hello, Copilot!".
                    Use the run_in_sandbox tool for every command. Do not try to access files outside the task container, use network access, or publish changes.
                    """,
                    AgentTimeout,
                    cancellationToken);
                agentInProgress = false;
                evidence.AgentFinished = true;
                evidence.FinalResponse = finalMessage?.Data.Content;
                progress?.Report("Running the protected acceptance check and capturing the change.");
                var acceptance = await WslFixtureRunner.RunProcessAsync(
                    taskContainer,
                    [
                        "/bin/sh",
                        "-c",
                        "set -e; [ \"$(/bin/sh \"$1\")\" = \"$2\" ]; printf 'ACCEPTANCE_PASS\\n'",
                        "sh",
                        SourcePath,
                        RequestedOutput
                    ],
                    CommandTimeout,
                    cancellationToken,
                    requiredMarker: "ACCEPTANCE_PASS");
                evidence.AcceptanceOutput = acceptance.StandardOutput;
                evidence.AcceptancePassed = acceptance.ExitCode == 0
                    && acceptance.StandardOutput.Contains("ACCEPTANCE_PASS", StringComparison.Ordinal);

                var source = await ReadSourceAsync(taskContainer, cancellationToken);
                evidence.ResultingSource = source;
                evidence.Patch = CreatePatch(OriginalSource, source);
                evidence.Outcome = evidence.AcceptancePassed && !string.Equals(
                    NormalizeLines(OriginalSource),
                    NormalizeLines(source),
                    StringComparison.Ordinal)
                    ? FixtureOutcome.Completed
                    : FixtureOutcome.Failed;
                if (evidence.Outcome == FixtureOutcome.Failed)
                {
                    evidence.Error = evidence.AcceptancePassed
                        ? "The acceptance check passed, but the repository edit did not change the fixture."
                        : "The protected acceptance check did not pass.";
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            evidence.Outcome = FixtureOutcome.Cancelled;
            evidence.Error = "The Copilot run was cancelled.";
        }
        catch (TimeoutException exception) when (evidence.AgentStarted)
        {
            evidence.Outcome = FixtureOutcome.TimedOut;
            evidence.Error = exception.Message;
        }
        catch (Exception exception)
        {
            evidence.Outcome = evidence.AgentStarted ? FixtureOutcome.Failed : FixtureOutcome.Blocked;
            evidence.Error = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            try
            {
                progress?.Report("Stopping the Copilot session and cleaning up owned WSL resources.");
            }
            catch (Exception exception)
            {
                cleanupErrors.Add($"progress reporting: {exception.Message}");
            }

            if (copilotSession is not null && agentInProgress)
            {
                try
                {
                    await copilotSession.AbortAsync();
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add($"Copilot session abort: {exception.Message}");
                }
            }

            if (taskContainer is not null)
            {
                CaptureCleanupError(cleanupErrors, "task container", () => StopAndDeleteContainer(taskContainer));
                CaptureCleanupError(cleanupErrors, "task container handle", taskContainer.Dispose);
            }

            if (copilotSession is not null)
            {
                await CaptureCleanupErrorAsync(cleanupErrors, "Copilot session", copilotSession.DisposeAsync);
            }

            if (client is not null)
            {
                await CaptureCleanupErrorAsync(cleanupErrors, "Copilot runtime", client.DisposeAsync);
            }

            sandboxCommandGate?.Dispose();

            if (wslSession is not null)
            {
                if (sessionStartAttempted)
                {
                    try
                    {
                        wslSession.Terminate();
                        sessionTerminated = true;
                    }
                    catch (Exception exception)
                    {
                        cleanupErrors.Add($"WSL session: {exception.Message}");
                    }
                }

                CaptureCleanupError(cleanupErrors, "WSL session handle", wslSession.Dispose);
            }

            if (runRootCreated && (wslSession is null || sessionTerminated))
            {
                CaptureCleanupError(cleanupErrors, "run working directory",
                    () => Directory.Delete(runRoot, recursive: true));
            }
            else if (runRootCreated)
            {
                cleanupErrors.Add($"Run resources were retained because WSL termination was not confirmed: {runRoot}");
            }

            if (variableSet)
            {
                CaptureCleanupError(cleanupErrors, "synthetic credential environment canary",
                    () => Environment.SetEnvironmentVariable(credentialVariable, previousCredentialValue));
            }

            if (canaryCreated)
            {
                CaptureCleanupError(cleanupErrors, "synthetic canary directory",
                    () => Directory.Delete(canaryDirectory, recursive: true));
            }
        }

        lock (eventLock)
        {
            evidence.Events = [.. capturedEvents];
            evidence.ReportedStrategies = reportedModels.Count == 0
                ? null
                : reportedModels.Order(StringComparer.Ordinal).ToArray();
        }

        evidence.UsageStatus = evidence.Events.Any(evt => evt.Type == "assistant.usage")
            ? "Reported"
            : "NotReported";
        if (cleanupErrors.Count != 0)
        {
            evidence.Outcome = FixtureOutcome.Failed;
            evidence.CleanupErrors = cleanupErrors;
        }

        evidence.CompletedUtc = DateTimeOffset.UtcNow;
        string evidencePath = await WslFixtureRunner.SaveEvidenceAsync(localAppData, runId, evidence);
        string summary = evidence.Outcome switch
        {
            FixtureOutcome.Completed => "The approved Copilot attempt passed the protected acceptance check.",
            FixtureOutcome.Blocked => $"Execution was blocked: {evidence.Error}",
            FixtureOutcome.Cancelled => "The Copilot run was cancelled and its owned resources were cleaned up.",
            FixtureOutcome.TimedOut => "The Copilot run timed out and its task container was stopped.",
            _ => $"The Copilot run failed: {evidence.Error ?? "cleanup failed."}"
                + (cleanupErrors.Count == 0 ? string.Empty : $" Cleanup: {string.Join("; ", cleanupErrors)}")
        };
        return new(evidence.Outcome, summary, evidencePath, evidence.Patch);
    }

    private static CopilotClient CreateClient(string copilotHome, string workingDirectory) =>
        new(new CopilotClientOptions
        {
            BaseDirectory = copilotHome,
            WorkingDirectory = workingDirectory,
            Mode = CopilotClientMode.CopilotCli,
            UseLoggedInUser = true
        });

    private static AIFunction CreateSandboxTool(
        WslContainer container,
        SemaphoreSlim commandGate,
        IProgress<string>? progress,
        CancellationToken runCancellationToken) =>
        CopilotTool.DefineTool(
            async ([Description("A shell command to run only inside the isolated task container.")] string command,
                CancellationToken toolCancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(command) || command.Length > 8_192)
                {
                    throw new ArgumentException("The sandbox command must be between 1 and 8192 characters.", nameof(command));
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    runCancellationToken,
                    toolCancellationToken);
                await commandGate.WaitAsync(linked.Token);
                try
                {
                    progress?.Report("Running a command inside the isolated task container.");
                    var result = await WslFixtureRunner.RunProcessAsync(
                        container,
                        ["/bin/sh", "-c", command],
                        CommandTimeout,
                        linked.Token);
                    return $"Exit code: {result.ExitCode}\n{result.StandardOutput}{result.StandardError}";
                }
                finally
                {
                    commandGate.Release();
                }
            },
            factoryOptions: new AIFunctionFactoryOptions
            {
                Name = "run_in_sandbox",
                Description = "Run one bounded shell command inside the isolated Linux task container."
            });

    private static async Task WriteFixtureAsync(WslContainer container, CancellationToken cancellationToken)
    {
        string source = Convert.ToBase64String(Encoding.UTF8.GetBytes(OriginalSource));
        var result = await WslFixtureRunner.RunProcessAsync(
            container,
            [
                "/bin/sh",
                "-c",
                "mkdir -p /workspace/project/src && printf '%s' \"$1\" | /bin/busybox base64 -d > \"$2\" && chmod +x \"$2\" && printf 'FIXTURE_READY\\n'",
                "sh",
                source,
                SourcePath
            ],
            CommandTimeout,
            cancellationToken,
            requiredMarker: "FIXTURE_READY");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"The task fixture could not be prepared: {result.StandardError}");
        }
    }

    private static async Task<string> ReadSourceAsync(WslContainer container, CancellationToken cancellationToken)
    {
        var result = await WslFixtureRunner.RunProcessAsync(
            container,
            ["/bin/sh", "-c", "test \"$(wc -c < \"$1\")\" -le 16384 && /bin/busybox base64 \"$1\"; printf '\\nSOURCE_DONE\\n'", "sh", SourcePath],
            CommandTimeout,
            cancellationToken,
            requiredMarker: "SOURCE_DONE");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"The resulting task file could not be captured: {result.StandardError}");
        }

        string encoded = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.Equals(line, "SOURCE_DONE", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The resulting task file was empty or could not be decoded.");
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The resulting task file was not valid base64.", exception);
        }
    }

    private static string CreatePatch(string original, string result)
    {
        string[] oldLines = NormalizeLines(original).Split('\n');
        string[] newLines = NormalizeLines(result).Split('\n');
        var patch = new StringBuilder()
            .AppendLine("--- a/src/greeting.sh")
            .AppendLine("+++ b/src/greeting.sh")
            .AppendLine($"@@ -1,{oldLines.Length} +1,{newLines.Length} @@");
        foreach (string line in oldLines)
        {
            patch.Append('-').AppendLine(line);
        }

        foreach (string line in newLines)
        {
            patch.Append('+').AppendLine(line);
        }

        return patch.ToString();
    }

    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

    private static string GetCopilotHome()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new IOException("The local application-data directory is unavailable.");
        }

        return Path.Combine(localAppData, "compareModels", "Copilot");
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

    private static async Task CaptureCleanupErrorAsync(
        List<string> errors,
        string resource,
        Func<ValueTask> cleanup)
    {
        try
        {
            await cleanup();
        }
        catch (Exception exception)
        {
            errors.Add($"{resource}: {exception.Message}");
        }
    }

    private sealed class BoundaryException(string message) : Exception(message);
}

/// <summary>Identifies a model strategy available through the signed-in account.</summary>
public sealed record CopilotModelStrategy(string Id, string Name);

/// <summary>Describes the terminal result of one approved Copilot attempt.</summary>
public sealed record CopilotRunResult(
    FixtureOutcome Outcome,
    string Summary,
    string EvidencePath,
    string? Patch);

internal sealed record CopilotBoundaryChecks(
    string ContainerConfiguration,
    string HostFileCanary,
    string CredentialFileCanary,
    string CredentialEnvironmentCanary,
    string NetworkControl,
    string BlockedNetwork,
    string? ImageId);

internal sealed record CopilotRunEvent(DateTimeOffset CapturedUtc, string Type, string Payload);

internal sealed class CopilotRunEvidence
{
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public string Windows { get; init; } = string.Empty;
    public string DotNet { get; init; } = string.Empty;
    public string WslContainersPackage { get; init; } = string.Empty;
    public string CopilotSdkPackage { get; init; } = string.Empty;
    public string CopilotCliRuntime { get; init; } = string.Empty;
    public string ImageReference { get; init; } = string.Empty;
    public string RequestedStrategy { get; init; } = string.Empty;
    public string[]? ReportedStrategies { get; set; }
    public string? WslVersion { get; set; }
    public string? WslContainerService { get; set; }
    public string Prerequisites { get; set; } = "NotRun";
    public string? PrerequisiteDetails { get; set; }
    public CopilotBoundaryChecks? BoundaryChecks { get; set; }
    public bool BoundaryVerified { get; set; }
    public bool AgentStarted { get; set; }
    public bool AgentFinished { get; set; }
    public bool AcceptancePassed { get; set; }
    public string? AcceptanceOutput { get; set; }
    public string? FinalResponse { get; set; }
    public string? ResultingSource { get; set; }
    public string? Patch { get; set; }
    public string UsageStatus { get; set; } = "NotReported";
    public List<CopilotRunEvent> Events { get; set; } = [];
    public FixtureOutcome Outcome { get; set; } = FixtureOutcome.Blocked;
    public string? Error { get; set; }
    public List<string> CleanupErrors { get; set; } = [];
}
