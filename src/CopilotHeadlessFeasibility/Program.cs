using System.Text.Json;
using GitHub.Copilot;

if (args is ["--mcp-stub"])
{
    await RunStubAsync();
    return 0;
}

if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var uri)
    || uri.Scheme != Uri.UriSchemeHttp || !System.Net.IPAddress.TryParse(uri.Host, out var address)
    || !System.Net.IPAddress.IsLoopback(address) || !string.IsNullOrEmpty(uri.UserInfo)
    || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
    || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPILOT_CONNECTION_TOKEN")))
{
    Console.Error.WriteLine("Provide a loopback HTTP server URL and COPILOT_CONNECTION_TOKEN.");
    return 2;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
try
{
    await using var client = new CopilotClient(new()
    {
        Connection = RuntimeConnection.ForUri(args[0], Environment.GetEnvironmentVariable("COPILOT_CONNECTION_TOKEN"))
    });
    await client.StartAsync(timeout.Token);
    var status = await client.GetStatusAsync(timeout.Token);
    Console.WriteLine($"CLI: {status.Version}; protocol: {status.ProtocolVersion}");
    if (status.Version != "1.0.112" || status.ProtocolVersion != 3)
    {
        Console.Error.WriteLine("Unexpected CLI or protocol version.");
        return 1;
    }

    try
    {
        var models = await client.ListModelsAsync(timeout.Token);
        Console.WriteLine($"Model listing: verified ({models.Count})");
        foreach (var model in models)
            Console.WriteLine($"{model.Id}\tpolicy={model.Policy?.State.ToString() ?? "unknown"}\tmultiplier={model.Billing?.Multiplier.ToString() ?? "unknown"}");
    }
    catch (Exception) when (!timeout.IsCancellationRequested)
    {
        Console.WriteLine("Model listing: unverified (authentication or RPC failure).");
    }

    var catalog = await client.Rpc.Tools.ListAsync("gpt-4.1", timeout.Token);
    Console.WriteLine("gpt-4.1 built-in catalog: " + string.Join(", ", catalog.Tools.Select(tool => tool.Name)));

    await using var session = await client.CreateSessionAsync(new()
    {
        Model = "gpt-4.1",
        AvailableTools = ["view", "edit", "create", "apply_patch", "glob", "grep", "mcp:sandbox-run_command"],
        DisabledMcpServers = ["github-mcp-server", "githubiq"],
        IncludedBuiltInAgents = [],
        EnableSkills = false,
        EnableSessionStore = false,
        EnableHostGitOperations = false,
        SkipCustomInstructions = true,
        EnableConfigDiscovery = false,
        EnableOnDemandInstructionDiscovery = false,
        EnableFileHooks = false,
        InfiniteSessions = new() { Enabled = false },
        McpServers = new Dictionary<string, McpServerConfig>
        {
            ["sandbox"] = new McpStdioServerConfig
            {
                Command = "dotnet",
                Args = [typeof(Program).Assembly.Location, "--mcp-stub"],
                Tools = ["*"]
            }
        }
    }, timeout.Token);
    var metadata = await session.Rpc.Tools.GetCurrentMetadataAsync(timeout.Token);
    Console.WriteLine($"Session tool metadata before inference: {(metadata?.Tools is { Count: > 0 } tools ? string.Join(", ", tools.Select(tool => tool.Name)) : "empty; effective list unverified")}");
    Console.WriteLine("No prompt sent. No permission handler configured.");
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("Spike failed or timed out; no error details printed to avoid exposing credentials.");
    return 1;
}

static async Task RunStubAsync()
{
    string? line;
    while ((line = await Console.In.ReadLineAsync()) is not null)
    {
        using var request = JsonDocument.Parse(line);
        var root = request.RootElement;
        if (!root.TryGetProperty("id", out var id))
            continue;
        var method = root.GetProperty("method").GetString();
        object result = method switch
        {
            "initialize" => new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } }, serverInfo = new { name = "sandbox", version = "1.0" } },
            "tools/list" => new { tools = new[] { new { name = "run_command", description = "Non-executing stub", inputSchema = new { type = "object", properties = new { } } } } },
            "tools/call" => new { isError = true, content = new[] { new { type = "text", text = "Stub does not execute commands." } } },
            _ => new { }
        };
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
    }
}
