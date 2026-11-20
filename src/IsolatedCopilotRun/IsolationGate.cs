using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;

namespace IsolatedCopilotRun;

public sealed record GateCheck(string Name, bool Passed, string Detail);

public sealed class GateFailure(string message) : Exception(message);

/// <summary>
/// Verifies the isolation controls of an <see cref="IsolatedEnvironment"/> with positive and
/// negative cases. Any failed or unverifiable control fails the gate (fail closed).
/// </summary>
public sealed class IsolationGate(
    IsolatedEnvironment environment,
    PreparedImage agentImage,
    PreparedImage taskImage,
    EvidenceStore evidence,
    string connectionToken,
    Action<string> log)
{
    public const string AgentDrop = "/opt/copilot/drop-workload";
    public const string ExecutorDrop = "/opt/sandbox/drop-workload";
    public const string TokenVariable = "COPILOT_GITHUB_TOKEN";
    public const string ToolCheckModel = "gpt-4.1";

    // Prints "H <pid> <pattern>" for each environment match and "U <pid>" for each unreadable environment.
    private const string ScanFunction =
        "scan() { for f in /proc/[0-9]*/environ; do p=${f%/environ}; p=${p#/proc/}; " +
        "if e=$( { tr '\\0' '\\n' < \"$f\"; } 2>/dev/null ); then for k in \"$@\"; do " +
        "printf '%s\\n' \"$e\" | grep -q -- \"^$k\" && echo \"H $p $k\"; done; else echo \"U $p\"; fi; done; }; ";

    private const string EnvironmentScan = ScanFunction + "scan \"$1\"";

    private const string CommandLineScan =
        "for f in /proc/[0-9]*/cmdline; do tr '\\0' ' ' < \"$f\" 2>/dev/null; echo; done | grep -c \"^$1\" || true";

    private const string NetworkProbe =
        "ip=$(getent hosts host.wslc.internal | awk '$1 ~ /^[0-9.]+$/ {print $1; exit}'); [ -n \"$ip\" ] || ip=\"$2\"; " +
        "echo \"IP=$ip\"; timeout 5 bash -c 'exec 3<>/dev/tcp/$0/$1 && printf \"GET / HTTP/1.0\\r\\n\\r\\n\" >&3 && cat <&3' \"$ip\" \"$1\" " +
        "| grep -q OK && echo REACHED || echo BLOCKED";

    private readonly List<GateCheck> _checks = [];

    public IReadOnlyList<GateCheck> Checks => _checks;

    /// <summary>Model used to initialize the tool-surface session (no inference is sent).</summary>
    public string ToolModel { get; init; } = ToolCheckModel;

    public bool Passed => _checks.Count > 0 && _checks.All(check => check.Passed);

    private Container Agent => environment.Agent.Container;

    private Container Executor => environment.Executor;

    /// <summary>Full gate, run with a synthetic GitHub token whose value may be passed to probes.</summary>
    public async Task RunFullAsync(string syntheticToken, CancellationToken cancellationToken)
    {
        await CheckPrivilegeAsync(cancellationToken);
        await Check("token.agent-present", () => TokenPresentAsync(syntheticToken, cancellationToken));
        await Check("token.executor-absent", () => TokenAbsentInExecutorAsync(syntheticToken, cancellationToken));
        await CheckCommonAsync(cancellationToken);
        await Check("mailbox.negative-cases", () => MailboxNegativeCasesAsync(cancellationToken));
        await Check("network.executor-blocked", () => NetworkAsync(cancellationToken));
        await Check("tools.surface", () => ToolSurfaceAsync(cancellationToken, unauthenticated: true));
        await Check("workload.kill", () => WorkloadKillAsync(cancellationToken));
        await Check("evidence.host-files", () => HostFilesAsync());
    }

    /// <summary>
    /// Checks on the live Run environment holding the real token. No token value is ever passed
    /// into a container: presence and absence are checked by variable name only.
    /// </summary>
    public async Task RunPreflightAsync(string taskNetwork, CancellationToken cancellationToken)
    {
        await CheckPrivilegeAsync(cancellationToken);
        await Check("token.agent-present", () => TokenPresentAsync(null, cancellationToken));
        await Check("token.executor-absent", () => TokenAbsentInExecutorAsync(null, cancellationToken));
        await CheckCommonAsync(cancellationToken);
        await Check("tools.surface", () => ToolSurfaceAsync(cancellationToken));
        if (taskNetwork == "blocked")
        {
            await Check("network.executor-blocked", () => NetworkAsync(cancellationToken));
        }
        else if (taskNetwork == "unrestricted")
        {
            await Check("network.executor-unrestricted", () => UnrestrictedNetworkExecutorAsync(cancellationToken));
        }
        else
        {
            await Check("network.policy", () => throw new GateFailure($"Unsupported Task network policy '{taskNetwork}'."));
        }
    }

    private async Task CheckPrivilegeAsync(CancellationToken cancellationToken)
    {
        await Check("privilege.agent", () => PrivilegeAsync(Agent, environment.AgentContainerName, agentImage,
            ContainerNetworkingMode.Bridged, AgentDrop, cancellationToken));
        await Check("privilege.executor", () => PrivilegeAsync(Executor, environment.ExecutorContainerName, taskImage,
            null, ExecutorDrop, cancellationToken));
        await Check("privilege.cli-process", () => CliProcessStateAsync(cancellationToken));
    }

    private async Task CheckCommonAsync(CancellationToken cancellationToken)
    {
        await Check("process.visibility", () => ProcessVisibilityAsync(cancellationToken));
        await Check("mounts.agent", () => MountsAsync(Agent,
            new() { ["/workspace"] = "rw", ["/mailbox/requests"] = "rw", ["/mailbox/responses"] = "ro" }, cancellationToken));
        await Check("mounts.executor", () => MountsAsync(Executor, new() { ["/workspace"] = "rw" }, cancellationToken));
        await Check("paths.write-probes", () => WriteProbesAsync(cancellationToken));
        await Check("mailbox.round-trip", () => MailboxRoundTripAsync(cancellationToken));
        await Check("connection-token", () => ConnectionTokenAsync(cancellationToken));
    }

    private async Task Check(string name, Func<Task<string>> control)
    {
        GateCheck check;
        try
        {
            check = new(name, true, await control());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            check = new(name, false, $"{exception.GetType().Name}: {exception.Message}");
        }

        _checks.Add(check);
        log($"  [{(check.Passed ? "PASS" : "FAIL")}] {check.Name}: {check.Detail}");
    }

    public JsonObject ToJson() => new()
    {
        ["passed"] = Passed,
        ["checks"] = new JsonArray(_checks.Select(check => (JsonNode)new JsonObject
        {
            ["name"] = check.Name,
            ["passed"] = check.Passed,
            ["detail"] = check.Detail
        }).ToArray())
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new GateFailure(message);
        }
    }

    private async Task<string> PrivilegeAsync(
        Container container,
        string sessionName,
        PreparedImage image,
        ContainerNetworkingMode? network,
        string drop,
        CancellationToken cancellationToken)
    {
        var root = Wsl.Inspect(container);
        string raw = root.GetRawText();
        Require(!evidence.ContainsSecret(raw), "Inspect() output contains a registered secret.");
        var host = root.GetProperty("HostConfig");
        Require(host.GetProperty("CapDrop").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["ALL"]),
            "CapDrop is not exactly ALL.");
        var added = host.GetProperty("CapAdd").EnumerateArray()
            .Select(value => value.GetString()!.Replace("CAP_", "", StringComparison.Ordinal)).Order().ToArray();
        Require(added.SequenceEqual(Wsl.Capabilities.Order()), $"CapAdd is {string.Join(',', added)}.");
        Require(!host.TryGetProperty("Privileged", out var privileged) || privileged.ValueKind == JsonValueKind.False,
            "Inspect() reports a privileged container.");
        if (root.GetProperty("Config").TryGetProperty("Entrypoint", out var entrypoint)
            && entrypoint.ValueKind == JsonValueKind.Array)
        {
            Require(!entrypoint.EnumerateArray().Any(), "The image defines an ENTRYPOINT.");
        }

        Require(root.GetProperty("Image").GetString() == image.ImageId, "Container image ID does not match the built image.");
        if (network is { } mode)
        {
            Require(string.Equals(host.GetProperty("NetworkMode").GetString(), mode == ContainerNetworkingMode.Bridged ? "bridge" : "none",
                StringComparison.OrdinalIgnoreCase), $"Unexpected network mode {host.GetProperty("NetworkMode").GetString()}.");
        }

        string id = root.GetProperty("Id").GetString()!;
        string daemon = (await RunImages.WslcAsync(cancellationToken, "--session", sessionName, "system", "session", "run",
            "docker", "inspect", "--format", "{{.HostConfig.Privileged}}", id)).Trim();
        Require(daemon == "false", $"Daemon reports Privileged={daemon}.");
        string checkState = drop[..drop.LastIndexOf('/')] + "/check-state";
        var workload = await Wsl.ProbeAsync(container, [drop, "/bin/sh", checkState], cancellationToken);
        Require(workload.ExitCode == 0 && workload.Output.Contains("STATE_OK"), $"Workload state check failed: {workload.Output.Trim()}");
        var asRoot = await Wsl.ProbeAsync(container, ["/bin/sh", checkState], cancellationToken);
        Require(asRoot.ExitCode != 0 && !asRoot.Output.Contains("STATE_OK"), "Negative control: check-state passed as root.");
        return $"CapDrop=ALL CapAdd={string.Join(',', added)} privileged=false(daemon) image={image.ImageId[..19]} " +
               $"{workload.Output.Split('\n')[0].Trim()}; root control rejected";
    }

    private async Task<string> CliProcessStateAsync(CancellationToken cancellationToken)
    {
        var result = await Wsl.ProbeAsync(Agent, ["/bin/sh", "-c",
            "for d in /proc/[0-9]*; do if tr '\\0' ' ' < $d/cmdline 2>/dev/null | grep -q '^/opt/copilot/copilot '; then " +
            "awk '/^Uid:/{u=$2} /^CapEff:/{c=$2} /^CapBnd:/{b=$2} /^NoNewPrivs:/{n=$2} END{print u, c, b, n}' $d/status; fi; done"],
            cancellationToken);
        string[] lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Require(lines.Length >= 1, "The CLI process was not found.");
        Require(lines.All(line => line == "1000 0000000000000000 0000000000000000 1"),
            $"CLI process state is not workload/no-caps/no-new-privs: {string.Join(" | ", lines)}");
        return $"{lines.Length} CLI process(es): uid=1000 CapEff=0 CapBnd=0 NoNewPrivs=1";
    }

    private async Task<string> TokenPresentAsync(string? value, CancellationToken cancellationToken)
    {
        string pattern = value is null ? $"{TokenVariable}=" : $"{TokenVariable}={value}$";
        var result = await Wsl.WorkloadProbeAsync(Agent, AgentDrop, EnvironmentScan, cancellationToken, pattern);
        var scan = ParseScan(result.Output);
        int count = scan.Hits.Count;
        Require(count >= 1, $"Positive control: token not found in the agent's process environments ({result.Output.Trim()}).");
        return $"{count} agent process(es) hold {TokenVariable}{(value is null ? "" : " with the synthetic value")}";
    }

    private sealed record ScanResult(List<(string Pid, string Pattern)> Hits, HashSet<string> Unreadable, string? Canary);

    private static ScanResult ParseScan(string output)
    {
        var scan = new ScanResult([], [], null);
        string? canary = null;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ', 3);
            switch (parts)
            {
                case ["H", var pid, var pattern]: scan.Hits.Add((pid, pattern)); break;
                case ["U", var pid]: scan.Unreadable.Add(pid); break;
                case ["C", var pid]: canary = pid; break;
                default: throw new GateFailure($"Unexpected environment scan output: {line}");
            }
        }

        return scan with { Canary = canary };
    }

    private async Task<string> TokenAbsentInExecutorAsync(string? value, CancellationToken cancellationToken)
    {
        string canary = $"canary{Guid.NewGuid():N}";
        string[] patterns = [$"{TokenVariable}=", "GH_TOKEN=", "GITHUB_TOKEN=", .. value is null ? Array.Empty<string>() : [$"{TokenVariable}={value}$"]];
        const string workloadScript = ScanFunction +
            "env COPILOT_GITHUB_TOKEN=\"$1\" sleep 30 & c=$!; sleep 0.5; echo \"C $c\"; shift; scan \"$@\"; kill $c";

        // UID 1000 cannot read root-owned environments and root (no CAP_SYS_PTRACE) cannot read
        // UID 1000 ones, so scan as both and require every process to be covered by one of them.
        var workload = ParseScan((await Wsl.WorkloadProbeAsync(Executor, ExecutorDrop, workloadScript, cancellationToken,
            [canary, .. patterns])).Output);
        var root = ParseScan((await Wsl.ProbeAsync(Executor, ["/bin/sh", "-c", ScanFunction + "scan \"$@\"", "sh", .. patterns],
            cancellationToken)).Output);

        Require(workload.Canary is not null && workload.Hits.Contains((workload.Canary, patterns[0])),
            "Positive control: the executor scan did not detect the canary process.");
        var leaks = workload.Hits.Where(hit => hit.Pid != workload.Canary).Concat(root.Hits).ToList();
        Require(leaks.Count == 0, $"Token variables found in executor processes: {string.Join(", ", leaks)}");
        var uncovered = workload.Unreadable.Intersect(root.Unreadable).ToList();
        Require(uncovered.Count == 0, $"Executor processes unreadable by both scans: {string.Join(", ", uncovered)}");

        var executorFiles = await Wsl.ProbeAsync(Executor, ["/bin/sh", "-c", "test ! -e /opt/copilot && test ! -e /mailbox && echo ABSENT"],
            cancellationToken);
        Require(executorFiles.Output.Contains("ABSENT"), "The executor can see /opt/copilot or /mailbox.");
        return $"no token variable in any executor process (scanned as uid 1000 and root; canary detected as positive control{(value is null ? "" : "; synthetic value absent")}); /opt/copilot and /mailbox absent";
    }

    private async Task<string> ProcessVisibilityAsync(CancellationToken cancellationToken)
    {
        var agent = await Wsl.WorkloadProbeAsync(Agent, AgentDrop, CommandLineScan, cancellationToken, "/opt/copilot/copilot ");
        var executor = await Wsl.WorkloadProbeAsync(Executor, ExecutorDrop, CommandLineScan, cancellationToken, "/opt/copilot/copilot ");
        Require(int.TryParse(agent.Output.Trim(), out int seen) && seen >= 1, "Positive control: the agent cannot see the CLI process.");
        Require(executor.Output.Trim() == "0", $"The executor can see the CLI process ({executor.Output.Trim()}).");
        return $"agent sees {seen} CLI process(es); executor sees 0";
    }

    private static async Task<string> MountsAsync(Container container, Dictionary<string, string> expected, CancellationToken cancellationToken)
    {
        var result = await Wsl.ProbeAsync(container, ["/bin/sh", "-c",
            "awk '{for(i=7;i<=NF;i++) if($i==\"-\"){print $5, $6, $(i+1); break}}' /proc/self/mountinfo"], cancellationToken);
        var mounts = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' '))
            .Where(parts => parts.Length == 3)
            .ToList();
        var workspace = mounts.LastOrDefault(parts => parts[0] == PermissionPolicy.Workspace)
            ?? throw new GateFailure("/workspace is not mounted.");
        string shareType = workspace[2];
        var shared = mounts.Where(parts => parts[2] == shareType)
            .ToDictionary(parts => parts[0], parts => parts[1].Split(',')[0]);
        Require(shared.Count == expected.Count && expected.All(pair => shared.TryGetValue(pair.Key, out var mode) && mode == pair.Value),
            $"Host shares ({shareType}) are {string.Join(", ", shared.Select(pair => $"{pair.Key}:{pair.Value}"))}.");
        return $"{shareType} shares exactly {string.Join(", ", shared.Select(pair => $"{pair.Key}:{pair.Value}"))}";
    }

    private async Task<string> WriteProbesAsync(CancellationToken cancellationToken)
    {
        string marker = $"shared-{Guid.NewGuid():N}";
        const string temp = ".00000000000000000000000000000000.tmp";
        var agent = await Wsl.WorkloadProbeAsync(Agent, AgentDrop,
            $"(touch /mailbox/requests/{temp} && rm /mailbox/requests/{temp} && echo requests=rw) 2>/dev/null || echo requests=ro; " +
            "(touch /mailbox/responses/.probe && echo responses=rw) 2>/dev/null || echo responses=ro; " +
            "(touch /opt/copilot/.probe && echo opt=rw) 2>/dev/null || echo opt=ro; " +
            "(printf %s \"$1\" > /workspace/.gate-shared && echo workspace=rw) 2>/dev/null || echo workspace=ro",
            cancellationToken, marker);
        string agentModes = string.Join(' ', agent.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Require(agentModes == "requests=rw responses=ro opt=ro workspace=rw", $"Agent write probes: {agentModes}");
        var executor = await Wsl.WorkloadProbeAsync(Executor, ExecutorDrop,
            "cat /workspace/.gate-shared; echo; (touch /workspace/.gate-exec && rm /workspace/.gate-exec && echo workspace=rw) 2>/dev/null " +
            "|| echo workspace=ro; rm -f /workspace/.gate-shared", cancellationToken);
        string[] lines = executor.Output.Split('\n', StringSplitOptions.TrimEntries);
        Require(lines.Length >= 2 && lines[0] == marker, "The executor did not see the agent's workspace file.");
        Require(lines[1] == "workspace=rw", "The executor workload user cannot write the workspace.");
        Require(!File.Exists(Path.Combine(environment.Paths.Workspace, ".gate-shared")), "The shared probe file was not removed.");
        return $"agent: {agentModes}; executor read the agent's file and can write /workspace";
    }

    private async Task<string> MailboxRoundTripAsync(CancellationToken cancellationToken)
    {
        int before = environment.Relay.Processed;
        string[] lines =
        [
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"gate","version":"1"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"run_command","arguments":{"command":"printf RELAY_OK; echo; id -u; test -e /opt/copilot && echo AGENT_FILES || echo NO_AGENT_FILES"}}}"""
        ];
        var result = await Wsl.ProbeAsync(Agent, [AgentDrop, "/bin/sh", "-c", "printf '%s\\n' \"$@\" | /opt/copilot/mailbox-mcp", "sh", .. lines],
            cancellationToken, seconds: 120);
        string output = result.Output;
        Require(output.Contains("run_command"), "tools/list did not advertise run_command.");
        Require(output.Contains("exit code: 0") && output.Contains("RELAY_OK") && output.Contains("\\n1000\\n")
            && output.Contains("NO_AGENT_FILES"), $"Unexpected round-trip result: {output.Trim()}");
        Require(environment.Relay.Processed == before + 1, "The relay did not record exactly one processed request.");
        return "mailbox-mcp (agent) -> host relay -> executor as uid 1000 without /opt/copilot -> response";
    }

    private async Task<string> MailboxNegativeCasesAsync(CancellationToken cancellationToken)
    {
        string staging = Path.Combine(environment.Paths.Work, "gate-staging");
        Directory.CreateDirectory(staging);
        int rejectedBefore = environment.Relay.Rejected;
        string Id() => Guid.NewGuid().ToString("N");
        string Request(string id, string command, string extra = "") =>
            $$"""{"id":"{{id}}","command":"{{command}}"{{extra}}}""";
        void Drop(string name, string body)
        {
            string temporary = Path.Combine(staging, name);
            File.WriteAllText(temporary, body);
            File.Move(temporary, Path.Combine(environment.Paths.Requests, name));
        }

        string replayId = Id();
        Drop($"{replayId}.json", Request(replayId, "true"));
        await WaitForAsync(() => File.Exists(Path.Combine(environment.Paths.Responses, $"{replayId}.json")), "replay seed response", cancellationToken);
        string oversizeId = Id(), malformedId = Id(), mismatchId = Id(), extraId = Id(), symlinkId = Id(), directoryId = Id();
        Drop("bad-name.json", Request(Id(), "touch /workspace/.canary-name"));
        Drop($"{oversizeId}.json", Request(oversizeId, "touch /workspace/.canary-size", $",\"pad\":\"{new string('x', MailboxRelay.MaxRequestBytes)}\""));
        Drop($"{malformedId}.json", "{\"id\":\"" + malformedId + "\",\"command\":\"touch /workspace/.canary-json\"");
        Drop($"{mismatchId}.json", Request(Id(), "touch /workspace/.canary-id"));
        Drop($"{extraId}.json", Request(extraId, "touch /workspace/.canary-extra", ",\"user\":\"root\""));
        Drop($"{replayId}.json", Request(replayId, "touch /workspace/.canary-replay"));
        var links = await Wsl.WorkloadProbeAsync(Agent, AgentDrop,
            "ln -s /etc/passwd /mailbox/requests/$1.json 2>&1 && echo LINK_CREATED; mkdir /mailbox/requests/$2.json 2>&1 && echo DIR_CREATED",
            cancellationToken, symlinkId, directoryId);
        int expected = 6 + (links.Output.Contains("LINK_CREATED") ? 1 : 0) + (links.Output.Contains("DIR_CREATED") ? 1 : 0);
        await WaitForAsync(() => !Directory.EnumerateFileSystemEntries(environment.Paths.Requests).Any(), "requests to drain", cancellationToken);
        Directory.Delete(staging, recursive: true);
        int rejected = environment.Relay.Rejected - rejectedBefore;
        Require(rejected == expected, $"Expected {expected} rejections, saw {rejected}.");
        string[] canaries = Directory.GetFiles(environment.Paths.Workspace, ".canary-*");
        Require(canaries.Length == 0, $"Rejected requests executed: {string.Join(", ", canaries.Select(Path.GetFileName))}");
        foreach (string id in new[] { oversizeId, malformedId, extraId })
        {
            var response = JsonNode.Parse(File.ReadAllText(Path.Combine(environment.Paths.Responses, $"{id}.json")))!;
            Require(response["status"]!.GetValue<string>() == "rejected", $"Request {id} was not answered as rejected.");
        }

        var replay = JsonNode.Parse(File.ReadAllText(Path.Combine(environment.Paths.Responses, $"{replayId}.json")))!;
        Require(replay["status"]!.GetValue<string>() == "completed", "The replayed request overwrote the original response.");
        return $"{rejected} rejected (bad name, oversize, malformed, id mismatch, extra property, replay" +
               $"{(links.Output.Contains("LINK_CREATED") ? ", symlink" : ", symlink creation refused by mount")}" +
               $"{(links.Output.Contains("DIR_CREATED") ? ", directory" : "")}); no canary command ran";
    }

    private async Task<string> NetworkAsync(CancellationToken cancellationToken)
    {
        using var endpoint = new LoopbackEndpoint();
        string executorMode = Wsl.Inspect(Executor).GetProperty("HostConfig").GetProperty("NetworkMode").GetString() ?? "";
        Require(executorMode == "none", $"Blocked Task executor network mode is '{executorMode}'.");
        var control = Wsl.CreateRoleContainer(environment.ExecutorSession, taskImage, $"{environment.Name}-netcontrol",
            ContainerNetworkingMode.Bridged,
            [new ContainerVolume(environment.Paths.Workspace, PermissionPolicy.Workspace, false)]);
        string address;
        try
        {
            string controlMode = Wsl.Inspect(control).GetProperty("HostConfig").GetProperty("NetworkMode").GetString() ?? "";
            Require(controlMode == "bridge", $"Unrestricted positive-control network mode is '{controlMode}'.");
            var positiveRequest = endpoint.WaitForRequestAsync(TimeSpan.FromSeconds(15));
            var positive = await Wsl.WorkloadProbeAsync(control, ExecutorDrop, NetworkProbe,
                cancellationToken, endpoint.Port.ToString(), "");
            bool accepted = await positiveRequest;
            Require(positive.Output.Contains("REACHED") && accepted,
                $"Positive control: a bridged container could not reach the host endpoint (accepted={accepted}, {positive.Output.Trim()}).");
            address = positive.Output.Split('\n').First(line => line.StartsWith("IP=", StringComparison.Ordinal))[3..].Trim();
            Require(IPAddress.TryParse(address, out _), "The bridged control reported no host address.");
        }
        finally
        {
            ContainerOperations.StopAndDeleteContainer(control);
            control.Dispose();
        }

        var blockedRequest = endpoint.WaitForRequestAsync(TimeSpan.FromSeconds(8));
        var blocked = await Wsl.WorkloadProbeAsync(Executor, ExecutorDrop, NetworkProbe, cancellationToken, endpoint.Port.ToString(), address);
        bool reached = await blockedRequest;
        Require(blocked.Output.Contains("BLOCKED") && !reached, "The executor reached the host endpoint.");
        return $"explicit unrestricted control (bridge) reached {address}:{endpoint.Port}; blocked executor (none) could not";
    }

    private async Task<string> UnrestrictedNetworkExecutorAsync(CancellationToken cancellationToken)
    {
        string mode = Wsl.Inspect(Executor).GetProperty("HostConfig").GetProperty("NetworkMode").GetString() ?? "";
        Require(mode == "bridge", $"Unrestricted Task executor network mode is '{mode}'.");
        using var endpoint = new LoopbackEndpoint();
        var request = endpoint.WaitForRequestAsync(TimeSpan.FromSeconds(15));
        var result = await Wsl.WorkloadProbeAsync(Executor, ExecutorDrop, NetworkProbe, cancellationToken, endpoint.Port.ToString(), "");
        Require(result.Output.Contains("REACHED") && await request,
            $"Unrestricted Task executor could not reach the host endpoint ({result.Output.Trim()}).");
        return $"live Task executor uses bridge and reached host endpoint on port {endpoint.Port}";
    }

    private async Task<string> ConnectionTokenAsync(CancellationToken cancellationToken)
    {
        await using (var wrong = new CopilotClient(new() { Connection = RuntimeConnection.ForUri(environment.Agent.Url, $"wrong-{Guid.NewGuid():N}") }))
        {
            bool rejected;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await wrong.StartAsync(timeout.Token);
                await wrong.GetStatusAsync(timeout.Token);
                rejected = false;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                rejected = true;
            }

            Require(rejected, "The CLI server accepted a wrong connection token.");
        }

        await using var client = await environment.Agent.ConnectAsync(connectionToken, cancellationToken);
        var status = await client.GetStatusAsync(cancellationToken);
        Require(status.Version == RunImages.CliVersion && status.ProtocolVersion == 3,
            $"Unexpected CLI {status.Version} protocol {status.ProtocolVersion}.");
        return $"wrong token rejected; correct token connected through 127.0.0.1:{environment.Agent.HostPort} (CLI {status.Version}, protocol {status.ProtocolVersion})";
    }

    private async Task<string> ToolSurfaceAsync(CancellationToken cancellationToken, bool unauthenticated = false)
    {
        AgentRuntime runtime = environment.Agent;
        string token = connectionToken;
        AgentRuntime? probe = null;
        if (unauthenticated)
        {
            token = GateRunner.NewSecret("conn");
            evidence.AddSecret(token);
            probe = await AgentRuntime.StartAsync(environment.AgentSession, agentImage,
                $"{environment.Name}-tool-probe", environment.Paths, "", token, cancellationToken);
            runtime = probe;
        }

        try
        {
            await using var client = await runtime.ConnectAsync(token, cancellationToken);
            await using var session = await client.CreateSessionAsync(AgentRuntime.CreateSessionConfig(ToolModel,
                (_, _) => Task.FromResult(PermissionPolicy.ToDecision((false, "gate session"))), null), cancellationToken);
            string[] tools = await AgentRuntime.GetModelVisibleToolsAsync(session, cancellationToken);
            Require(AgentRuntime.IsValidToolSurface(tools), $"Model-visible tools: {string.Join(", ", tools)}");
            var mcp = await Wsl.WorkloadProbeAsync(runtime.Container, AgentDrop,
                "found=0; leaked=0; for f in /proc/[0-9]*/cmdline; do " +
                "tr '\\0' ' ' < \"$f\" 2>/dev/null | grep -q '^/opt/copilot/mailbox-mcp' || continue; " +
                "found=$((found+1)); p=${f%/cmdline}; " +
                "tr '\\0' '\\n' < \"$p/environ\" 2>/dev/null | grep -q '^COPILOT_GITHUB_TOKEN=' && leaked=1; " +
                "done; echo \"$found $leaked\"", cancellationToken);
            string[] state = mcp.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Require(state is [var count, "0"] && int.TryParse(count, out int mcpCount) && mcpCount == 1,
                $"MCP token-isolation probe failed: {mcp.Output.Trim()}.");
            return $"model-visible tools without inference: {string.Join(", ", tools)}; MCP process is token-free" +
                   (unauthenticated ? " (unauthenticated probe)" : "");
        }
        finally
        {
            if (probe is not null)
            {
                await probe.DisposeAsync();
            }
        }
    }

    private async Task<string> WorkloadKillAsync(CancellationToken cancellationToken)
    {
        var result = await Wsl.RunWorkloadCommandAsync(Executor, "setsid sleep 300 & sleep 300", TimeSpan.FromSeconds(2), cancellationToken);
        Require(result.Status == "timeout", $"Expected a timeout, got {result.Status}.");
        var survivors = await Wsl.ProbeAsync(Executor, ["/bin/sh", "-c", Wsl.CountWorkloadProcesses], cancellationToken);
        Require(survivors.Output.Trim() == "0", $"{survivors.Output.Trim()} workload processes survived.");
        return "timed-out command and its setsid child were killed; 0 workload processes remain";
    }

    private Task<string> HostFilesAsync()
    {
        var hits = evidence.ScanTree(environment.Paths.Workspace)
            .Concat(evidence.ScanTree(Path.Combine(environment.Paths.Work, "mailbox"))).ToList();
        Require(hits.Count == 0, $"Secrets found in host files: {string.Join(", ", hits)}");
        return Task.FromResult("no registered secret in workspace or mailbox files on the host");
    }

    private static async Task WaitForAsync(Func<bool> condition, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Require(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(200, cancellationToken);
        }
    }
}
