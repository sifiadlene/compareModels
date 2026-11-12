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
        IList<ContainerVolume>? volumes = null)
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
            PortMappings = []
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
}
