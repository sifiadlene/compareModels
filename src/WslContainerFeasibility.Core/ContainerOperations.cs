using System.Diagnostics;
using System.Text;
using Microsoft.WSL.Containers;

namespace WslContainerFeasibility;

internal static class ContainerOperations
{
    internal const string ImageReference =
        "docker.io/library/alpine@sha256:49340b346798875b5592e5d420241f2f9cfba5f47e62ebb1446a1b34734e898f";

    internal static Container CreateContainer(
        Session session,
        ContainerNetworkingMode networkingMode,
        string[] commandLine,
        IList<ContainerVolume>? volumes = null,
        IList<ContainerPortMapping>? portMappings = null)
    {
        var settings = new ContainerSettings(ImageReference)
        {
            Name = $"compare-models-feasibility-{Guid.NewGuid():N}",
            InitProcess = new ProcessSettings
            {
                CommandLine = commandLine,
                OutputMode = ProcessOutputMode.Event
            },
            NetworkingMode = networkingMode,
            EnableAutoRemove = false,
            EnableGpu = false,
            Privileged = false,
            Volumes = volumes ?? [],
            NamedVolumes = [],
            PortMappings = portMappings ?? []
        };

        return session.CreateContainer(settings);
    }

    internal static void StopAndDeleteContainer(Container container)
    {
        if (container.State == ContainerState.Running)
        {
            container.Stop(Signal.SIGKILL, TimeSpan.FromSeconds(5));
        }

        if (container.State != ContainerState.Deleted)
        {
            container.Delete(DeleteContainerOption.Force);
        }
    }

    internal static string ToWslPath(string windowsPath)
    {
        string root = Path.GetPathRoot(windowsPath)
            ?? throw new ArgumentException("Expected a rooted Windows path.", nameof(windowsPath));
        if (root.Length != 3 || root[1] != ':')
        {
            throw new ArgumentException("Expected a drive-letter Windows path.", nameof(windowsPath));
        }

        return $"/mnt/{char.ToLowerInvariant(root[0])}/{windowsPath[root.Length..].Replace('\\', '/')}";
    }

    internal static async Task<(int ExitCode, string StandardOutput, string StandardError)> InspectContainerThroughDaemonAsync(
        string sessionName,
        string containerId,
        StringBuilder evidence)
    {
        try
        {
            var result = await RunDaemonCommandAsync(
                sessionName,
                ["docker", "inspect", "--format", "{{.Id}}|{{.Name}}|{{.HostConfig.Privileged}}", containerId],
                TimeSpan.FromSeconds(15));
            evidence.AppendLine($"DaemonInspectExitCode={result.ExitCode}");
            evidence.AppendLine($"DaemonInspectOutput={result.StandardOutput.Trim()}");
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                evidence.AppendLine($"DaemonInspectError={result.StandardError.Trim()}");
            }
            return (result.ExitCode, result.StandardOutput, result.StandardError);
        }
        catch (Exception exception)
        {
            evidence.AppendLine($"DaemonInspectFailure={exception.Message}");
            throw;
        }
    }

    internal static async Task<CommandResult> RunDaemonCommandAsync(
        string sessionName, string[] arguments, TimeSpan limit)
    {
        string wslcPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wslc.exe");
        if (!File.Exists(wslcPath))
        {
            throw new FileNotFoundException("The WSL container CLI required for daemon inspection is missing.", wslcPath);
        }

        var startInfo = new ProcessStartInfo(wslcPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "--session", sessionName, "system", "session", "run" }.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The WSL container CLI did not start.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("Could not start the WSL container CLI.", exception);
        }

        Task<(string Text, bool Truncated)> standardOutput = ReadBoundedAsync(process.StandardOutput);
        Task<(string Text, bool Truncated)> standardError = ReadBoundedAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(standardOutput, standardError);
            throw new TimeoutException($"The same-session WSL container command exceeded {limit.TotalSeconds:0.#} seconds.");
        }

        var output = await standardOutput;
        var error = await standardError;
        if (output.Truncated || error.Truncated)
        {
            throw new InvalidOperationException("The same-session WSL container command exceeded its output limit.");
        }
        return new(process.ExitCode, output.Text, error.Text);
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader)
    {
        const int limit = 2 * 1024 * 1024;
        var output = new StringBuilder();
        var buffer = new char[4096];
        bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            int remaining = limit - output.Length;
            output.Append(buffer, 0, Math.Min(count, Math.Max(remaining, 0)));
            truncated |= count > remaining;
        }
        return (output.ToString(), truncated);
    }
}
