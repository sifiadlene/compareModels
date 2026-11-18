using System.Text.Json;

namespace IsolatedCopilotRun;

public sealed record TaskDefinition(
    string Id,
    string Directory,
    string Prompt,
    string Packages,
    string Network,
    string CheckCommand,
    int RunTimeoutMinutes)
{
    public string WorkspaceSource => Path.Combine(Directory, "workspace");

    public string ChecksSource => Path.Combine(Directory, "checks");

    public static TaskDefinition Load(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "task.json")));
        var root = document.RootElement;
        string network = root.GetProperty("network").GetString()!;
        if (network is not ("blocked" or "unrestricted"))
        {
            throw new InvalidDataException("Task network must be 'blocked' or 'unrestricted'.");
        }

        return new(
            root.GetProperty("id").GetString()!,
            Path.GetFullPath(directory),
            File.ReadAllText(Path.Combine(directory, "prompt.md")),
            root.GetProperty("packages").GetString()!,
            network,
            root.GetProperty("checkCommand").GetString()!,
            root.GetProperty("runTimeoutMinutes").GetInt32());
    }

    /// <summary>Creates a fresh workspace from the Task sources (workspace files plus checks).</summary>
    public void CreateWorkspace(string target)
    {
        CopyDirectory(WorkspaceSource, target);
        CopyDirectory(ChecksSource, Path.Combine(target, "checks"));
    }

    public static void CopyDirectory(string source, string target)
    {
        System.IO.Directory.CreateDirectory(target);
        foreach (string file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).LinkTarget is not null)
            {
                continue;
            }

            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}

public sealed record RunPaths(string Root)
{
    public string Work => Path.Combine(Root, "work");

    public string Evidence => Path.Combine(Root, "evidence");

    public string Workspace => Path.Combine(Work, "workspace");

    public string Pristine => Path.Combine(Work, "pristine");

    public string Requests => Path.Combine(Work, "mailbox", "requests");

    public string Responses => Path.Combine(Work, "mailbox", "responses");

    public string Evaluation => Path.Combine(Work, "evaluation");

    public string SessionStorage(string role) => Path.Combine(Work, "sessions", role);

    public RunPaths Child(string name) => new(Path.Combine(Work, name));

    public void CreateMailbox()
    {
        Directory.CreateDirectory(Requests);
        Directory.CreateDirectory(Responses);
    }
}
