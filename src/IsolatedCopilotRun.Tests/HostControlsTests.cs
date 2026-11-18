using System.Text;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using IsolatedCopilotRun;
using Xunit;

namespace IsolatedCopilotRun.Tests;

public sealed class HostControlsTests : IDisposable
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private readonly string _root = Directory.CreateTempSubdirectory("icr-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ToolSurface_AllowsModelSpecificWorkspaceToolsButRejectsUnsafeOrNonEditingSurfaces()
    {
        string[] terraTools = ["apply_patch", "glob", "rg", "sandbox-run_command", "view"];

        Assert.True(AgentRuntime.IsValidToolSurface(terraTools));
        Assert.True(AgentRuntime.IsValidToolSurface(["create", "edit", "glob", "grep", "sandbox-run_command", "view"]));
        Assert.False(AgentRuntime.IsValidToolSurface(["glob", "rg", "sandbox-run_command", "view"]));
        Assert.False(AgentRuntime.IsValidToolSurface([.. terraTools, "bash"]));
        Assert.False(AgentRuntime.IsValidToolSurface([.. terraTools, "web_fetch"]));
        Assert.False(AgentRuntime.IsValidToolSurface(["apply_patch", "glob", "rg", "view"]));
    }

    [Theory]
    [InlineData("sandbox", "run_command", true)]
    [InlineData("sandbox", "sandbox-run_command", true)]
    [InlineData("sandbox", "run_command_extra", false)]
    [InlineData("other", "run_command", false)]
    [InlineData("other", "sandbox-run_command", false)]
    public void Permissions_AllowOnlySandboxCommandIncludingCliPrefixedName(string server, string tool, bool expected)
    {
        var request = new PermissionRequestMcp
        {
            ServerName = server,
            ToolName = tool,
            ToolTitle = tool,
            ReadOnly = false
        };

        Assert.Equal(expected, PermissionPolicy.Decide(request).Approved);
    }

    [Theory]
    [InlineData("/workspace", true)]
    [InlineData("/workspace/slugify.py", true)]
    [InlineData("/workspace/./a/../b.txt", true)]
    [InlineData("/workspace/../etc/passwd", false)]
    [InlineData("/workspacex/file", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("workspace/file", false)]
    [InlineData("/workspace/a\\b", false)]
    public void IsInsideWorkspace_AllowsOnlyPathsUnderWorkspace(string path, bool expected) =>
        Assert.Equal(expected, PermissionPolicy.IsInsideWorkspace(path));

    [Theory]
    [InlineData("{\"id\":\"" + Id + "\",\"command\":\"ls\"}", true)]
    [InlineData("{\"id\":\"" + Id + "\",\"command\":\"ls\",\"timeoutSeconds\":5}", true)]
    [InlineData("{\"id\":\"ffffffffffffffffffffffffffffffff\",\"command\":\"ls\"}", false)]
    [InlineData("{\"id\":\"" + Id + "\",\"command\":\"ls\",\"extra\":1}", false)]
    [InlineData("{\"id\":\"" + Id + "\",\"command\":\"ls\",\"timeoutSeconds\":601}", false)]
    [InlineData("{\"id\":\"" + Id + "\",\"command\":\"\"}", false)]
    [InlineData("[1]", false)]
    [InlineData("not json", false)]
    public void TryParse_AcceptsOnlyWellFormedRequests(string json, bool expected) =>
        Assert.Equal(expected, MailboxRelay.TryParse(Encoding.UTF8.GetBytes(json), Id, out _, out _, out _));

    [Fact]
    public async Task Relay_RunsValidRequestOnceAndRejectsReplayAndBadNames()
    {
        var (requests, responses) = (Path.Combine(_root, "req"), Path.Combine(_root, "resp"));
        Directory.CreateDirectory(requests);
        Directory.CreateDirectory(responses);
        var commands = new List<string>();
        var relay = new MailboxRelay(requests, responses,
            (command, _, _) => { commands.Add(command); return Task.FromResult(new RelayCommandResult("completed", 0, "ok", false)); },
            _ => { });

        string request = $"{{\"id\":\"{Id}\",\"command\":\"echo hi\"}}";
        File.WriteAllText(Path.Combine(requests, $"{Id}.json"), request);
        File.WriteAllText(Path.Combine(requests, "evil.json"), request);
        await relay.ProcessPendingAsync(CancellationToken.None);

        File.WriteAllText(Path.Combine(requests, $"{Id}.json"), request);
        File.WriteAllText(Path.Combine(requests, $"{"a".PadRight(32, 'a')}.json"), new string(' ', MailboxRelay.MaxRequestBytes + 1));
        await relay.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(["echo hi"], commands);
        Assert.Equal(1, relay.Processed);
        Assert.Equal(3, relay.Rejected);
        Assert.Empty(Directory.EnumerateFileSystemEntries(requests));
        Assert.Equal("completed", JsonNode.Parse(File.ReadAllText(Path.Combine(responses, $"{Id}.json")))!["status"]!.GetValue<string>());
        Assert.Equal("rejected", JsonNode.Parse(File.ReadAllText(Path.Combine(responses, $"{"a".PadRight(32, 'a')}.json")))!["status"]!.GetValue<string>());
    }

    [Fact]
    public void Evidence_RefusesSecretsAndScanFindsThem()
    {
        var evidence = new EvidenceStore(Path.Combine(_root, "evidence"));
        evidence.AddSecret("ghu_secretvalue123");

        Assert.Throws<SecretLeakException>(() => evidence.Write("a.txt", "token=ghu_secretvalue123"));
        Assert.Throws<SecretLeakException>(() => evidence.AppendLine("b.txt", "ghu_secretvalue123"));
        Assert.False(File.Exists(Path.Combine(evidence.Directory, "a.txt")));

        string tree = Path.Combine(_root, "tree");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "clean.txt"), "nothing here");
        File.WriteAllText(Path.Combine(tree, "leak.txt"), "x ghu_secretvalue123 y");
        Assert.Equal(["leak.txt"], evidence.ScanTree(tree));
    }

    [Fact]
    public void UsageSummary_LeavesUnreportedUsageUnknown()
    {
        var usage = new UsageSummary().ToJson();

        Assert.Equal("unknown", usage["telemetryStatus"]!.GetValue<string>());
        Assert.Null(usage["modelCalls"]);
        Assert.Null(usage["models"]);
        Assert.Null(usage["inputTokens"]);
        Assert.Null(usage["reportedModelMultiplierCost"]);
    }

    [Fact]
    public void UsageSummary_PreservesReportedUnitsAndTokenSubsets()
    {
        var summary = new UsageSummary();
        summary.Add(new AssistantUsageData
        {
            Model = "gpt-4.1",
            InputTokens = 12,
            OutputTokens = 7,
            ReasoningTokens = 3,
            CacheReadTokens = 4,
            CacheWriteTokens = 2,
            Cost = 1.5
        });

        var usage = summary.ToJson();

        Assert.Equal(1, usage["modelCalls"]!.GetValue<int>());
        Assert.Equal("gpt-4.1", usage["models"]![0]!.GetValue<string>());
        Assert.Equal(12, usage["inputTokens"]!.GetValue<long>());
        Assert.Equal(7, usage["outputTokens"]!.GetValue<long>());
        Assert.Equal(3, usage["reasoningTokens"]!.GetValue<long>());
        Assert.Equal(4, usage["cacheReadTokens"]!.GetValue<long>());
        Assert.Equal(2, usage["cacheWriteTokens"]!.GetValue<long>());
        Assert.Equal(1.5, usage["reportedModelMultiplierCost"]!.GetValue<double>());
        Assert.Equal("model multiplier (not currency)", usage["reportedModelMultiplierCostUnit"]!.GetValue<string>());
    }
}
