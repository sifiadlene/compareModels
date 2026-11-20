using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsolatedCopilotRun;

public sealed class SecretLeakException(string artifact)
    : Exception($"A secret was detected in '{artifact}'; the artifact was not written.");

/// <summary>Writes Run evidence and refuses to persist any registered secret value.</summary>
public sealed class EvidenceStore
{
    private readonly List<string> _secrets = [];
    private readonly Lock _gate = new();

    public EvidenceStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    public void AddSecret(string value)
    {
        if (value.Length < 8)
        {
            throw new ArgumentException("Secrets must be at least 8 characters to be scanned reliably.");
        }

        using (_gate.EnterScope())
        {
            _secrets.Add(value);
        }
    }

    public bool ContainsSecret(string text)
    {
        using (_gate.EnterScope())
        {
            return _secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal));
        }
    }

    public string Write(string name, string text)
    {
        if (ContainsSecret(text))
        {
            throw new SecretLeakException(name);
        }

        string path = Path.Combine(Directory, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    public void WriteJson(string name, JsonNode node) =>
        Write(name, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    public void AppendLine(string name, string line)
    {
        if (ContainsSecret(line))
        {
            throw new SecretLeakException(name);
        }

        using (_gate.EnterScope())
        {
            File.AppendAllText(Path.Combine(Directory, name), line.ReplaceLineEndings(" ") + "\n", new UTF8Encoding(false));
        }
    }

    /// <summary>Scans every file under <paramref name="root"/> for registered secrets.</summary>
    public IReadOnlyList<string> ScanTree(string root)
    {
        var hits = new List<string>();
        foreach (string file in System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is not null || info.Length > 64 * 1024 * 1024)
            {
                continue;
            }

            if (ContainsSecret(Encoding.UTF8.GetString(File.ReadAllBytes(file))))
            {
                hits.Add(Path.GetRelativePath(root, file));
            }
        }

        return hits;
    }
}
