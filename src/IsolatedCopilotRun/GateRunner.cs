using System.Security.Cryptography;
using Microsoft.WSL.Containers;

namespace IsolatedCopilotRun;

/// <summary>Runs the full isolation gate in a throwaway environment holding a synthetic token.</summary>
public static class GateRunner
{
    public static string NewSecret(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public static async Task<IsolationGate> RunAsync(
        string name,
        RunPaths paths,
        PreparedImage agentImage,
        PreparedImage taskImage,
        EvidenceStore evidence,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string syntheticToken = NewSecret("ghu_synthetic");
        string connectionToken = NewSecret("conn");
        evidence.AddSecret(syntheticToken);
        evidence.AddSecret(connectionToken);
        log($"Gate: starting {name} (synthetic token, executor network none)...");
        var environment = await IsolatedEnvironment.StartAsync(name, paths, agentImage, taskImage,
            ContainerNetworkingMode.None, syntheticToken, connectionToken,
            entry => evidence.AppendLine("gate-relay.jsonl", entry.ToJsonString()), cancellationToken);
        IsolationGate gate;
        System.Text.Json.Nodes.JsonObject environmentIdentity;
        try
        {
            gate = new IsolationGate(environment, agentImage, taskImage, evidence, connectionToken, log);
            await gate.RunFullAsync(syntheticToken, cancellationToken);
            environmentIdentity = environment.IdentityToJson();
        }
        finally
        {
            await environment.DisposeAsync();
            foreach (string step in environment.CleanupLog)
            {
                log($"Gate cleanup: {step}");
            }
        }

        var report = gate.ToJson();
        report["environment"] = environmentIdentity;
        report["cleanup"] = new System.Text.Json.Nodes.JsonArray(environment.CleanupLog.Select(step => (System.Text.Json.Nodes.JsonNode)step).ToArray());
        evidence.WriteJson("gate.json", report);
        if (environment.CleanupLog.Any(step => step.Contains("FAILED", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Gate environment cleanup failed; see gate.json.");
        }

        TryDelete(paths.Root, log);
        log($"Gate: {(gate.Passed ? "PASSED" : "FAILED")} ({gate.Checks.Count(check => check.Passed)}/{gate.Checks.Count}).");
        return gate;
    }

    public static void TryDelete(string directory, Action<string> log)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"Could not delete {directory}: {exception.Message}");
        }
    }
}
