using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;

namespace IsolatedCopilotRun;

public sealed record CommandOutput(int ExitCode, string Output);

/// <summary>WSL container helpers shared by the gate and the Run.</summary>
public static class Wsl
{
    public static readonly string[] Capabilities = ["SETGID", "SETPCAP", "SETUID"];
    public const int OutputLimit = 64 * 1024;
    public static readonly Dictionary<string, string> WorkloadEnvironment = new()
    {
        ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
        ["HOME"] = "/home/workload",
        ["LANG"] = "C.UTF-8"
    };

    public static Session StartSession(string name, string storage, uint memoryMb = 2048)
    {
        Directory.CreateDirectory(storage);
        var session = new Session(new SessionSettings(name, storage)
        {
            CpuCount = 2,
            MemorySizeInMB = memoryMb,
            HostLoopback = "host.wslc.internal"
        });
        try
        {
            session.Start();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public static async Task LoadImageAsync(Session session, PreparedImage image, CancellationToken cancellationToken)
    {
        await session.LoadImageAsync(image.TarPath).AsTask(cancellationToken);
        if (!session.GetImages().Any(info => info.Name == image.Tag || info.Name == $"docker.io/library/{image.Tag}"))
        {
            throw new InvalidOperationException($"Image {image.Tag} was not loaded into session.");
        }
    }

    public static Container CreateRoleContainer(
        Session session,
        PreparedImage image,
        string name,
        ContainerNetworkingMode network,
        IList<ContainerVolume> volumes,
        IList<ContainerPortMapping>? ports = null)
    {
        var container = session.CreateContainer(new ContainerSettings(image.Tag)
        {
            Name = name,
            Privileged = false,
            CapabilityDrops = ["ALL"],
            CapabilityAdditions = Capabilities,
            NetworkingMode = network,
            EnableAutoRemove = false,
            EnableGpu = false,
            Volumes = volumes,
            NamedVolumes = [],
            PortMappings = ports ?? [],
            InitProcess = new ProcessSettings
            {
                CommandLine = ["/bin/sleep", "infinity"],
                OutputMode = ProcessOutputMode.Event
            }
        });
        try
        {
            container.Start();
            return container;
        }
        catch
        {
            ContainerOperations.StopAndDeleteContainer(container);
            container.Dispose();
            throw;
        }
    }

    public static JsonElement Inspect(Container container)
    {
        using var document = JsonDocument.Parse(container.Inspect());
        return document.RootElement.Clone();
    }

    /// <summary>Runs a command as root (limited capabilities) for host-side probes.</summary>
    public static async Task<CommandOutput> ProbeAsync(
        Container container, string[] command, CancellationToken cancellationToken, int seconds = 30)
    {
        var result = await WslFixtureRunner.RunProcessAsync(
            container, command, TimeSpan.FromSeconds(seconds), cancellationToken,
            environment: new Dictionary<string, string>(WorkloadEnvironment));
        return new(result.ExitCode, result.StandardOutput + result.StandardError);
    }

    public static Task<CommandOutput> WorkloadProbeAsync(
        Container container, string dropWorkload, string script, CancellationToken cancellationToken, params string[] args) =>
        ProbeAsync(container, [dropWorkload, "/bin/sh", "-c", script, "sh", .. args], cancellationToken);

    /// <summary>
    /// Runs a relayed Task command as the workload user. On timeout or cancellation every
    /// workload-user process in the executor is killed and the absence of survivors is verified.
    /// </summary>
    public static async Task<RelayCommandResult> RunWorkloadCommandAsync(
        Container executor, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = executor.CreateProcess(new ProcessSettings
        {
            CommandLine = ["/opt/sandbox/drop-workload", "/bin/sh", "-c", command],
            EnvironmentVariables = new Dictionary<string, string>(WorkloadEnvironment),
            WorkingDirectory = PermissionPolicy.Workspace,
            OutputMode = ProcessOutputMode.Event
        });
        var output = new StringBuilder();
        bool truncated = false;
        var gate = new Lock();
        void Append(byte[] data)
        {
            using (gate.EnterScope())
            {
                string text = Encoding.UTF8.GetString(data);
                int room = OutputLimit - output.Length;
                if (text.Length > room)
                {
                    truncated = true;
                    text = text[..Math.Max(0, room)];
                }

                output.Append(text);
            }
        }

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputReceived += Append;
        process.ErrorReceived += Append;
        process.Exited += code => exited.TrySetResult(code);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        process.Start();
        try
        {
            int exitCode = await exited.Task.WaitAsync(linked.Token);
            using (gate.EnterScope())
            {
                return new("completed", exitCode, output.ToString(), truncated);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            await KillWorkloadAsync(executor);
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellationToken.ThrowIfCancellationRequested();
            using (gate.EnterScope())
            {
                return new("timeout", null, output.ToString(), truncated,
                    $"command exceeded {timeout.TotalSeconds:0} seconds; workload processes were killed");
            }
        }
    }

    public static async Task KillWorkloadAsync(Container executor)
    {
        await ProbeAsync(executor, ["/opt/sandbox/drop-workload", "/bin/sh", "-c", "kill -KILL -1 2>/dev/null; exit 0"],
            CancellationToken.None);
        var survivors = await ProbeAsync(executor, ["/bin/sh", "-c", CountWorkloadProcesses], CancellationToken.None);
        if (survivors.Output.Trim() != "0")
        {
            throw new InvalidOperationException($"Workload processes survived termination: {survivors.Output.Trim()}");
        }
    }

    // PID 1 is `sleep infinity`, which never reaps, so killed workloads remain as zombies (state Z).
    // Zombies run no code, so only live UID 1000 processes count as survivors.
    public const string CountWorkloadProcesses =
        "n=0; for s in /proc/[0-9]*/status; do u=$(awk '/^Uid:/{u=$2} /^State:/{z=$2} END{if(z!=\"Z\") print u}' \"$s\" 2>/dev/null); " +
        "[ \"$u\" = 1000 ] && n=$((n+1)); done; echo $n";

    /// <summary>Starts a long-lived workload process and optionally writes lines to its stdin.</summary>
    public static async Task<(Process Process, Func<string> Output)> StartWorkloadAsync(
        Container container,
        string[] command,
        string? readyMarker,
        IReadOnlyList<string>? stdinLines,
        CancellationToken cancellationToken)
    {
        var process = container.CreateProcess(new ProcessSettings
        {
            CommandLine = ["/opt/copilot/drop-workload", .. command],
            EnvironmentVariables = new Dictionary<string, string>(WorkloadEnvironment),
            EnableStandardInput = stdinLines is not null,
            OutputMode = ProcessOutputMode.Event
        });
        var output = new StringBuilder();
        var gate = new Lock();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Append(byte[] data)
        {
            using (gate.EnterScope())
            {
                if (output.Length < OutputLimit)
                {
                    output.Append(Encoding.UTF8.GetString(data));
                }

                if (readyMarker is not null && output.ToString().Contains(readyMarker, StringComparison.Ordinal))
                {
                    ready.TrySetResult();
                }
            }
        }

        process.OutputReceived += Append;
        process.ErrorReceived += Append;
        process.Exited += code => exited.TrySetResult(code);
        process.Start();
        if (stdinLines is not null)
        {
            using var stdin = process.GetInputStream();
            byte[] bytes = Encoding.UTF8.GetBytes(string.Concat(stdinLines.Select(line => line + "\n")));
            await stdin.WriteAsync(bytes.AsBuffer()).AsTask(cancellationToken);
            await stdin.FlushAsync().AsTask(cancellationToken);
        }

        string Snapshot()
        {
            using (gate.EnterScope())
            {
                return output.ToString();
            }
        }

        if (readyMarker is not null)
        {
            var completed = await Task.WhenAny(ready.Task, exited.Task, Task.Delay(TimeSpan.FromSeconds(60), cancellationToken));
            if (completed != ready.Task)
            {
                process.Dispose();
                throw new InvalidOperationException($"{command[0]} did not become ready: {Snapshot()}");
            }
        }

        return (process, Snapshot);
    }
}
