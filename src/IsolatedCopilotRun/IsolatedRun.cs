using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;

namespace IsolatedCopilotRun;

/// <summary>Gate and live Run commands.</summary>
public static class IsolatedRun
{
    public static string RunsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "compareModels", "IsolatedRuns");

    private const string Marker = "running.marker";

    public static async Task<int> GateAsync(string taskDirectory, CancellationToken cancellationToken)
    {
        RecoverInterruptedRuns();
        var (runId, paths, evidence, log) = Start("gate");
        try
        {
            var task = TaskDefinition.Load(taskDirectory);
            var (agentImage, taskImage) = await RunImages.PrepareForTaskAsync(task, log, cancellationToken);
            await WriteManifestAsync(evidence, task, agentImage, taskImage, null, cancellationToken);
            var gate = await GateRunner.RunAsync($"cmg-{runId[^8..]}", paths.Child("gate"), agentImage, taskImage, evidence, log,
                cancellationToken);
            Finish(paths, evidence, log, gate.Passed ? "GatePassed" : "Blocked", gate.Passed ? null : "isolation gate failed");
            return gate.Passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Finish(paths, evidence, log, exception is OperationCanceledException ? "Cancelled" : "Blocked", Describe(exception));
            return 1;
        }
    }

    public static async Task<int> RunAsync(string taskDirectory, string model, CancellationToken cancellationToken)
    {
        string? githubToken = Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(githubToken) || Environment.GetEnvironmentVariable("COMPAREMODELS_LIVE_RUN") != "1")
        {
            Console.Error.WriteLine("Blocked: set COPILOT_GITHUB_TOKEN (fine-grained, Copilot Requests only) and COMPAREMODELS_LIVE_RUN=1.");
            return 2;
        }

        RecoverInterruptedRuns();
        var (runId, paths, evidence, log) = Start("run");
        evidence.AddSecret(githubToken);
        string outcome = "Blocked";
        string? reason = null;
        IsolatedEnvironment? environment = null;
        var result = new JsonObject { ["model"] = model };
        try
        {
            var task = TaskDefinition.Load(taskDirectory);
            result["task"] = task.Id;
            var (agentImage, taskImage) = await RunImages.PrepareForTaskAsync(task, log, cancellationToken);
            await WriteManifestAsync(evidence, task, agentImage, taskImage, model, cancellationToken);

            var gate = await GateRunner.RunAsync($"cmg-{runId[^8..]}", paths.Child("gate"), agentImage, taskImage, evidence, log,
                cancellationToken);
            if (!gate.Passed)
            {
                reason = "isolation gate failed";
                return 1;
            }

            task.CreateWorkspace(paths.Workspace);
            task.CreateWorkspace(paths.Pristine);
            string connectionToken = GateRunner.NewSecret("conn");
            evidence.AddSecret(connectionToken);
            bool blocked = task.Network == "blocked";
            log($"Run: starting fresh agent and executor (executor network {task.Network})...");
            environment = await IsolatedEnvironment.StartAsync($"cmr-{runId[^8..]}", paths, agentImage, taskImage,
                blocked ? ContainerNetworkingMode.None : ContainerNetworkingMode.Bridged, githubToken, connectionToken,
                entry => evidence.AppendLine("relay.jsonl", entry.ToJsonString()), cancellationToken);
            result["environment"] = environment.IdentityToJson();

            await using var client = await environment.Agent.ConnectAsync(connectionToken, cancellationToken);
            var auth = await client.GetAuthStatusAsync(cancellationToken);
            if (!auth.IsAuthenticated)
            {
                reason = $"CLI is not authenticated ({auth.StatusMessage})";
                return 1;
            }

            var models = await client.ListModelsAsync(cancellationToken);
            evidence.WriteJson("models.json", new JsonArray(models.Select(info => (JsonNode)new JsonObject
            {
                ["id"] = info.Id,
                ["name"] = info.Name,
                ["policy"] = info.Policy?.State,
                ["modelMultiplier"] = info.Billing?.Multiplier
            }).ToArray()));
            var selected = models.FirstOrDefault(info => info.Id == model);
            string? policy = selected?.Policy?.State;
            if (selected is null || (policy is not null && !policy.Equals("enabled", StringComparison.OrdinalIgnoreCase)))
            {
                reason = selected is null ? $"model '{model}' is not listed" : $"model '{model}' policy is '{policy}'";
                evidence.Write("models.txt", string.Join('\n', models.Select(info => $"{info.Id}\t{info.Policy?.State}")));
                log($"Available model IDs: {string.Join(", ", models.Select(info => info.Id))}");
                return 1;
            }

            log("Run: preflight checks on the live environment...");
            var preflight = new IsolationGate(environment, agentImage, taskImage, evidence, connectionToken, log) { ToolModel = model };
            await preflight.RunPreflightAsync(task.Network, cancellationToken);
            evidence.WriteJson("preflight.json", preflight.ToJson());
            if (!preflight.Passed)
            {
                reason = "live preflight failed";
                return 1;
            }

            double? multiplier = selected.Billing?.Multiplier;
            result["authType"] = auth.AuthType;
            result["modelPolicy"] = policy;
            result["modelMultiplier"] = multiplier;
            Console.WriteLine();
            Console.WriteLine($"Ready: Task '{task.Id}', model '{model}' (multiplier {multiplier?.ToString() ?? "unknown"}), " +
                              $"timeout {task.RunTimeoutMinutes} min. This sends one billable Run.");
            Console.Write("Type RUN to start: ");
            if ((await Console.In.ReadLineAsync(cancellationToken))?.Trim() != "RUN")
            {
                reason = "not confirmed";
                return 1;
            }

            outcome = "Error";
            File.WriteAllText(Path.Combine(paths.Evidence, Marker), runId);
            (outcome, reason) = await SendAsync(environment, client, task, model, evidence, result, log, cancellationToken);
            await CapturePatchAsync(paths, evidence, result, log);
            var completedEnvironment = environment;
            await completedEnvironment.DisposeAsync();
            result["cleanup"] = new JsonArray(completedEnvironment.CleanupLog.Select(step => (JsonNode)step).ToArray());
            environment = null;
            if (completedEnvironment.CleanupLog.Any(step => step.Contains("FAILED", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Run environment cleanup failed; refusing to evaluate while it may remain active.");
            }

            await EvaluateAsync(runId, paths, task, taskImage, evidence, result, log);
            return outcome == "AgentFinished" ? 0 : 1;
        }
        catch (Exception exception)
        {
            outcome = exception is OperationCanceledException ? "Cancelled" : outcome == "Blocked" ? "Blocked" : "Error";
            reason = Describe(exception);
            return 1;
        }
        finally
        {
            if (environment is not null)
            {
                await environment.DisposeAsync();
                result["cleanup"] = new JsonArray(environment.CleanupLog.Select(step => (JsonNode)step).ToArray());
            }

            evidence.WriteJson("result.json", result);
            Finish(paths, evidence, log, outcome, reason);
        }
    }

    private static async Task<(string Outcome, string? Reason)> SendAsync(
        IsolatedEnvironment environment,
        CopilotClient client,
        TaskDefinition task,
        string model,
        EvidenceStore evidence,
        JsonObject result,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var usage = new UsageSummary();
        int processedBefore = environment.Relay.Processed;
        int rejectedBefore = environment.Relay.Rejected;
        bool leak = false;
        string? sessionError = null;
        void OnEvent(SessionEvent sessionEvent)
        {
            try
            {
                evidence.AppendLine("events.jsonl", sessionEvent.ToJson());
            }
            catch (SecretLeakException)
            {
                leak = true;
            }

            switch (sessionEvent)
            {
                case AssistantUsageEvent usageEvent:
                    usage.Add(usageEvent.Data);
                    break;
                case SessionErrorEvent error:
                    sessionError = error.Data.Message;
                    break;
            }
        }

        Task<GitHub.Copilot.Rpc.PermissionDecision> OnPermission(PermissionRequest request, PermissionInvocation _)
        {
            var decision = PermissionPolicy.Decide(request);
            try
            {
                evidence.AppendLine("permissions.jsonl", new JsonObject
                {
                    ["time"] = DateTimeOffset.UtcNow,
                    ["kind"] = request.GetType().Name,
                    ["approved"] = decision.Approved,
                    ["reason"] = decision.Reason
                }.ToJsonString());
            }
            catch (SecretLeakException)
            {
                leak = true;
            }

            return Task.FromResult(PermissionPolicy.ToDecision(decision));
        }

        await using var session = await client.CreateSessionAsync(
            AgentRuntime.CreateSessionConfig(model, OnPermission, OnEvent), cancellationToken);
        var started = Stopwatch.StartNew();
        string outcome;
        string? reason = null;
        log($"Run: prompt sent to {model}; waiting up to {task.RunTimeoutMinutes} min...");
        try
        {
            var reply = await session.SendAndWaitAsync(task.Prompt, TimeSpan.FromMinutes(task.RunTimeoutMinutes), cancellationToken);
            outcome = sessionError is null ? "AgentFinished" : "Error";
            reason = sessionError;
            if (reply is not null)
            {
                evidence.Write("final-message.md", reply.Data.Content ?? "");
            }
        }
        catch (TimeoutException)
        {
            outcome = "Timeout";
            reason = $"no completion within {task.RunTimeoutMinutes} minutes";
            await StopAsync(session, environment, log);
        }
        catch (OperationCanceledException)
        {
            outcome = "Cancelled";
            reason = "cancelled by the user";
            await StopAsync(session, environment, log);
        }
        catch (Exception exception)
        {
            outcome = "Error";
            reason = Describe(exception);
            await StopAsync(session, environment, log);
        }

        if (leak)
        {
            outcome = "Error";
            reason = "a registered secret appeared in session events or permissions; affected entries were not written";
        }

        result["durationSeconds"] = Math.Round(started.Elapsed.TotalSeconds, 1);
        result["usage"] = usage.ToJson();
        int processed = environment.Relay.Processed - processedBefore;
        result["relay"] = new JsonObject { ["processed"] = processed, ["rejected"] = environment.Relay.Rejected - rejectedBefore };
        log($"Run: {outcome} after {started.Elapsed.TotalSeconds:0}s; {usage.Calls} model call(s); relay processed {processed} command(s).");
        return (outcome, reason);
    }

    private static async Task StopAsync(CopilotSession session, IsolatedEnvironment environment, Action<string> log)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.AbortAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            log($"Abort failed: {Describe(exception)}");
        }

        await Wsl.KillWorkloadAsync(environment.Executor);
    }

    private static async Task CapturePatchAsync(RunPaths paths, EvidenceStore evidence, JsonObject result, Action<string> log)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = paths.Work
        };
        foreach (string argument in new[] { "-c", "core.autocrlf=false", "diff", "--no-index", "--binary", "pristine", "workspace" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var git = System.Diagnostics.Process.Start(startInfo)!;
        string patch = await git.StandardOutput.ReadToEndAsync();
        string error = await git.StandardError.ReadToEndAsync();
        await git.WaitForExitAsync();
        if (git.ExitCode > 1)
        {
            throw new InvalidOperationException($"git diff failed: {error}");
        }

        evidence.Write("changes.patch", patch);
        result["patchBytes"] = Encoding.UTF8.GetByteCount(patch);
        result["changedFiles"] = new JsonArray(patch.Split('\n')
            .Where(line => line.StartsWith("diff --git ", StringComparison.Ordinal))
            .Select(line => (JsonNode)line["diff --git ".Length..]).ToArray());
        log($"Run: captured patch ({patch.Length} characters).");
    }

    /// <summary>Runs the trusted checks against a copy of the result in a fresh, network-less container.</summary>
    private static async Task EvaluateAsync(
        string runId, RunPaths paths, TaskDefinition task, PreparedImage taskImage, EvidenceStore evidence, JsonObject result, Action<string> log)
    {
        TaskDefinition.CopyDirectory(paths.Workspace, paths.Evaluation);
        string checks = Path.Combine(paths.Evaluation, "checks");
        if (Directory.Exists(checks))
        {
            Directory.Delete(checks, recursive: true);
        }

        TaskDefinition.CopyDirectory(task.ChecksSource, checks);
        log("Evaluation: running trusted checks in a fresh container (network none)...");
        string sessionName = $"cme-{runId[^8..]}";
        string containerName = $"{sessionName}-eval";
        var evaluation = new JsonObject
        {
            ["status"] = "not-run",
            ["sessionName"] = sessionName,
            ["containerName"] = containerName,
            ["imageId"] = taskImage.ImageId
        };
        result["evaluation"] = evaluation;
        var session = Wsl.StartSession(sessionName, paths.SessionStorage("eval"));
        try
        {
            await Wsl.LoadImageAsync(session, taskImage, CancellationToken.None);
            var container = Wsl.CreateRoleContainer(session, taskImage, containerName, ContainerNetworkingMode.None,
                [new ContainerVolume(paths.Evaluation, PermissionPolicy.Workspace, false)]);
            try
            {
                var inspection = Wsl.Inspect(container);
                string networkMode = inspection.GetProperty("HostConfig").GetProperty("NetworkMode").GetString() ?? "";
                if (networkMode != "none" || inspection.GetProperty("Image").GetString() != taskImage.ImageId)
                {
                    throw new InvalidOperationException("Evaluation container image or network policy did not match the protected configuration.");
                }

                evaluation["containerId"] = inspection.GetProperty("Id").GetString();
                evaluation["networkMode"] = networkMode;
                var check = await Wsl.RunWorkloadCommandAsync(container, task.CheckCommand, TimeSpan.FromMinutes(10), CancellationToken.None);
                evidence.Write("evaluation.txt", check.Output);
                evaluation["status"] = check.Status;
                evaluation["exitCode"] = check.ExitCode;
                evaluation["passed"] = check.Status == "completed" && check.ExitCode == 0;
                log($"Evaluation: {(check.ExitCode == 0 ? "checks passed" : $"checks failed ({check.Status}, exit {check.ExitCode})")}.");
            }
            finally
            {
                ContainerOperations.StopAndDeleteContainer(container);
                container.Dispose();
            }
        }
        finally
        {
            session.Terminate();
            session.Dispose();
        }
    }

    private static (string RunId, RunPaths Paths, EvidenceStore Evidence, Action<string> Log) Start(string kind)
    {
        string runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{kind}-{Guid.NewGuid():N}"[..^24];
        var paths = new RunPaths(Path.Combine(RunsRoot, runId));
        var evidence = new EvidenceStore(paths.Evidence);
        void Log(string line)
        {
            Console.WriteLine(line);
            evidence.AppendLine("log.txt", $"{DateTimeOffset.UtcNow:O} {line}");
        }

        Log($"{kind} {runId}: evidence in {paths.Evidence}");
        return (runId, paths, evidence, Log);
    }

    private static async Task WriteManifestAsync(
        EvidenceStore evidence,
        TaskDefinition task,
        PreparedImage agent,
        PreparedImage taskImage,
        string? model,
        CancellationToken cancellationToken)
    {
        string wslVersion = await WslFixtureRunner.ReadWslVersionAsync(cancellationToken);
        var wslcVersion = WslcService.GetVersion();
        evidence.WriteJson("manifest.json", new JsonObject
        {
            ["task"] = task.Id,
            ["taskPackages"] = task.Packages,
            ["taskNetwork"] = task.Network,
            ["model"] = model,
            ["cliVersion"] = RunImages.CliVersion,
            ["cliTarballSha256"] = RunImages.CliTarballSha256,
            ["agentImage"] = new JsonObject { ["tag"] = agent.Tag, ["id"] = agent.ImageId, ["tarSha256"] = agent.TarSha256 },
            ["taskImage"] = new JsonObject { ["tag"] = taskImage.Tag, ["id"] = taskImage.ImageId, ["tarSha256"] = taskImage.TarSha256 },
            ["sdk"] = typeof(CopilotClient).Assembly.GetName().Version?.ToString(),
            ["dotnet"] = Environment.Version.ToString(),
            ["host"] = Environment.OSVersion.VersionString,
            ["wsl"] = wslVersion,
            ["wslContainerRuntime"] = $"{wslcVersion.Major}.{wslcVersion.Minor}.{wslcVersion.Revision}",
            ["wslContainerApiPackage"] = WslFixtureRunner.ApiPackageVersion
        });
    }

    private static void Finish(RunPaths paths, EvidenceStore evidence, Action<string> log, string outcome, string? reason)
    {
        GateRunner.TryDelete(paths.Work, log);
        var hits = evidence.ScanTree(paths.Evidence);
        evidence.WriteJson("outcome.json", new JsonObject
        {
            ["outcome"] = hits.Count == 0 ? outcome : "Error",
            ["reason"] = hits.Count == 0 ? reason : "secret found in evidence files",
            ["workDirectoryRemoved"] = !Directory.Exists(paths.Work),
            ["evidenceSecretScan"] = hits.Count == 0 ? "clean" : $"{hits.Count} file(s) contained a secret"
        });
        File.Delete(Path.Combine(paths.Evidence, Marker));
        log($"Outcome: {outcome}{(reason is null ? "" : $" ({reason})")}");
    }

    private static void RecoverInterruptedRuns()
    {
        if (!Directory.Exists(RunsRoot))
        {
            return;
        }

        foreach (string marker in Directory.EnumerateFiles(RunsRoot, Marker, SearchOption.AllDirectories))
        {
            string evidence = Path.GetDirectoryName(marker)!;
            File.WriteAllText(Path.Combine(evidence, "outcome.json"),
                new JsonObject { ["outcome"] = "Interrupted", ["reason"] = "the host stopped while the Run was active" }.ToJsonString());
            File.Delete(marker);
            Console.WriteLine($"Marked interrupted Run: {evidence}");
        }
    }

    private static string Describe(Exception exception) =>
        exception is SecretLeakException ? exception.Message : $"{exception.GetType().Name}: {exception.Message}";

}
