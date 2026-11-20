using System.Text.Json.Nodes;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;

namespace IsolatedCopilotRun;

/// <summary>
/// One agent session (CLI + mailbox MCP, holds the GitHub token) and one executor session
/// (Task tools, no token) sharing only the workspace and the host-relayed mailbox.
/// </summary>
public sealed class IsolatedEnvironment : IAsyncDisposable
{
    private readonly CancellationTokenSource _relayStop = new();
    private readonly List<Session> _sessions = [];
    private Task? _relayTask;

    private IsolatedEnvironment(string name, RunPaths paths)
    {
        Name = name;
        Paths = paths;
    }

    public string Name { get; }

    public RunPaths Paths { get; }

    public Session AgentSession { get; private set; } = null!;

    public Session ExecutorSession { get; private set; } = null!;

    public AgentRuntime Agent { get; private set; } = null!;

    public Container Executor { get; private set; } = null!;

    public MailboxRelay Relay { get; private set; } = null!;

    public string AgentContainerName => $"{Name}-agent";

    public string ExecutorContainerName => $"{Name}-executor";

    public List<string> CleanupLog { get; } = [];

    public JsonObject IdentityToJson() => new()
    {
        ["agentSession"] = $"{Name}-agent",
        ["agent"] = ContainerIdentity(Agent.Container),
        ["executorSession"] = $"{Name}-executor",
        ["executor"] = ContainerIdentity(Executor)
    };

    public static async Task<IsolatedEnvironment> StartAsync(
        string name,
        RunPaths paths,
        PreparedImage agentImage,
        PreparedImage taskImage,
        ContainerNetworkingMode executorNetwork,
        string githubToken,
        string connectionToken,
        Action<JsonObject> log,
        CancellationToken cancellationToken)
    {
        paths.CreateMailbox();
        Directory.CreateDirectory(paths.Workspace);
        var environment = new IsolatedEnvironment(name, paths);
        try
        {
            environment.AgentSession = environment.AddSession($"{name}-agent", paths.SessionStorage("agent"));
            environment.ExecutorSession = environment.AddSession($"{name}-executor", paths.SessionStorage("executor"));
            await Wsl.LoadImageAsync(environment.AgentSession, agentImage, cancellationToken);
            await Wsl.LoadImageAsync(environment.ExecutorSession, taskImage, cancellationToken);
            environment.Executor = Wsl.CreateRoleContainer(environment.ExecutorSession, taskImage,
                environment.ExecutorContainerName, executorNetwork,
                [new ContainerVolume(paths.Workspace, PermissionPolicy.Workspace, false)]);
            environment.Relay = new MailboxRelay(paths.Requests, paths.Responses,
                (command, timeout, token) => Wsl.RunWorkloadCommandAsync(environment.Executor, command, timeout, token), log);
            environment._relayTask = environment.Relay.RunAsync(environment._relayStop.Token);
            environment.Agent = await AgentRuntime.StartAsync(environment.AgentSession, agentImage,
                environment.AgentContainerName, paths, githubToken, connectionToken, cancellationToken);
            return environment;
        }
        catch
        {
            await environment.DisposeAsync();
            throw;
        }
    }

    private Session AddSession(string name, string storage)
    {
        var session = Wsl.StartSession(name, storage);
        _sessions.Add(session);
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        await Step("stop relay", async () =>
        {
            _relayStop.Cancel();
            if (_relayTask is not null)
            {
                try
                {
                    await _relayTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        });
        if (Agent is not null)
        {
            await Step("delete agent container", async () => await Agent.DisposeAsync());
        }

        if (Executor is not null)
        {
            await Step("delete executor container", () =>
            {
                ContainerOperations.StopAndDeleteContainer(Executor);
                Executor.Dispose();
                return Task.CompletedTask;
            });
        }

        foreach (var session in _sessions)
        {
            await Step("terminate session", () =>
            {
                session.Terminate();
                session.Dispose();
                return Task.CompletedTask;
            });
        }

        _relayStop.Dispose();
    }

    private async Task Step(string name, Func<Task> action)
    {
        try
        {
            await action();
            CleanupLog.Add($"{name}: ok");
        }
        catch (Exception exception)
        {
            CleanupLog.Add($"{name}: FAILED {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static JsonObject ContainerIdentity(Container container)
    {
        var inspection = Wsl.Inspect(container);
        return new JsonObject
        {
            ["name"] = inspection.GetProperty("Name").GetString(),
            ["id"] = inspection.GetProperty("Id").GetString(),
            ["imageId"] = inspection.GetProperty("Image").GetString(),
            ["networkMode"] = inspection.GetProperty("HostConfig").GetProperty("NetworkMode").GetString()
        };
    }
}
