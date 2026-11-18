using System.Net;
using System.Net.Sockets;
using GitHub.Copilot;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;
using WslProcess = Microsoft.WSL.Containers.Process;

namespace IsolatedCopilotRun;

/// <summary>The agent container: pinned headless CLI, loopback relay and SDK connection.</summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    public const int CliPort = 3000;
    public const ushort ContainerPort = 8080;
    public static readonly string[] SessionTools =
        ["view", "create", "edit", "apply_patch", "grep", "rg", "glob", "mcp:sandbox-run_command"];
    public static readonly string[] ExpectedModelTools =
        ["create", "edit", "glob", "grep", "sandbox-run_command", "view"];
    private static readonly string[] PatchModelTools =
        ["apply_patch", "glob", "rg", "sandbox-run_command", "view"];

    private readonly List<WslProcess> _processes = [];
    private Func<string>? _cliOutput;

    private AgentRuntime(Container container, int hostPort)
    {
        Container = container;
        HostPort = hostPort;
    }

    public Container Container { get; }

    public int HostPort { get; }

    public string Url => $"http://127.0.0.1:{HostPort}";

    public string CliOutput => _cliOutput?.Invoke() ?? "";

    public static async Task<AgentRuntime> StartAsync(
        Session session,
        PreparedImage image,
        string name,
        RunPaths paths,
        string githubToken,
        string connectionToken,
        CancellationToken cancellationToken)
    {
        int hostPort = ReservePort();
        var container = Wsl.CreateRoleContainer(session, image, name, ContainerNetworkingMode.Bridged,
        [
            new ContainerVolume(paths.Workspace, PermissionPolicy.Workspace, false),
            new ContainerVolume(paths.Requests, "/mailbox/requests", false),
            new ContainerVolume(paths.Responses, "/mailbox/responses", true)
        ],
        [
            new ContainerPortMapping((ushort)hostPort, ContainerPort, PortProtocol.TCP)
            {
                WindowsAddress = new Windows.Networking.HostName("127.0.0.1")
            }
        ]);
        var agent = new AgentRuntime(container, hostPort);
        try
        {
            var relay = await Wsl.StartWorkloadAsync(container,
                ["/usr/bin/socat", $"TCP-LISTEN:{ContainerPort},fork,reuseaddr", $"TCP:127.0.0.1:{CliPort}"],
                null, null, cancellationToken);
            agent._processes.Add(relay.Process);
            var cli = await Wsl.StartWorkloadAsync(container, ["/opt/copilot/start-cli"],
                $"listening on port {CliPort}", [githubToken, connectionToken], cancellationToken);
            agent._processes.Add(cli.Process);
            agent._cliOutput = cli.Output;
            return agent;
        }
        catch
        {
            await agent.DisposeAsync();
            throw;
        }
    }

    public async Task<CopilotClient> ConnectAsync(string connectionToken, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var client = new CopilotClient(new() { Connection = RuntimeConnection.ForUri(Url, connectionToken) });
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await client.StartAsync(timeout.Token);
                return client;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                last = exception;
                await client.DisposeAsync();
                await Task.Delay(500, cancellationToken);
            }
        }

        throw new InvalidOperationException("Could not connect to the agent CLI server.", last);
    }

    public static SessionConfig CreateSessionConfig(
        string model,
        Func<PermissionRequest, PermissionInvocation, Task<GitHub.Copilot.Rpc.PermissionDecision>> onPermission,
        Action<SessionEvent>? onEvent) => new()
    {
        Model = model,
        WorkingDirectory = PermissionPolicy.Workspace,
        AvailableTools = SessionTools,
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
        OnPermissionRequest = onPermission,
        OnEvent = onEvent,
        McpServers = new Dictionary<string, McpServerConfig>
        {
            ["sandbox"] = new McpStdioServerConfig
            {
                Command = "/opt/copilot/mailbox-mcp",
                Args = [],
                Tools = ["run_command"]
            }
        }
    };

    /// <summary>Initializes the session's tools without inference and returns the model-visible names.</summary>
    public static async Task<string[]> GetModelVisibleToolsAsync(CopilotSession session, CancellationToken cancellationToken)
    {
        await session.Rpc.Tools.InitializeAndValidateAsync(cancellationToken);
        var metadata = await session.Rpc.Tools.GetCurrentMetadataAsync(cancellationToken);
        return metadata?.Tools?.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray() ?? [];
    }

    internal static bool IsValidToolSurface(string[] tools) =>
        tools.SequenceEqual(ExpectedModelTools) || tools.SequenceEqual(PatchModelTools);

    public ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            process.Dispose();
        }

        ContainerOperations.StopAndDeleteContainer(Container);
        Container.Dispose();
        return ValueTask.CompletedTask;
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
