using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IsolatedCopilotRun;

public sealed record RelayCommandResult(string Status, int? ExitCode, string Output, bool Truncated, string? Reason = null);

public delegate Task<RelayCommandResult> CommandExecutor(string command, TimeSpan timeout, CancellationToken cancellationToken);

/// <summary>
/// Host-side relay: validates mailbox requests written by the agent container and executes them
/// only through the token-free executor. Requests are processed one at a time.
/// </summary>
public sealed partial class MailboxRelay(
    string requestsDirectory,
    string responsesDirectory,
    CommandExecutor execute,
    Action<JsonObject> log)
{
    public const int MaxRequestBytes = 64 * 1024;
    public const int MaxCommandLength = 16 * 1024;
    private static readonly TimeSpan StaleTemporaryFile = TimeSpan.FromSeconds(30);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    [GeneratedRegex("^[0-9a-f]{32}\\.json$")]
    private static partial Regex RequestName();

    [GeneratedRegex("^\\.[0-9a-f]{32}\\.tmp$")]
    private static partial Regex TemporaryName();

    public int Processed { get; private set; }

    public int Rejected { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await ProcessPendingAsync(cancellationToken);
            await Task.Delay(200, cancellationToken);
        }
    }

    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var entries = new DirectoryInfo(requestsDirectory).EnumerateFileSystemInfos()
            .OrderBy(entry => entry.CreationTimeUtc)
            .ToList();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessAsync(entry, cancellationToken);
        }
    }

    private async Task ProcessAsync(FileSystemInfo entry, CancellationToken cancellationToken)
    {
        string name = entry.Name;
        if (TemporaryName().IsMatch(name) && entry is FileInfo && entry.LinkTarget is null
            && DateTime.UtcNow - entry.LastWriteTimeUtc < StaleTemporaryFile)
        {
            return;
        }

        string? id = RequestName().IsMatch(name) ? name[..32] : null;
        if (entry is not FileInfo file || entry.LinkTarget is not null
            || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Reject(entry, id, null, "request is not a regular file");
            return;
        }

        if (id is null)
        {
            Reject(entry, null, null, "invalid request file name");
            return;
        }

        if (file.Length > MaxRequestBytes)
        {
            Reject(entry, id, null, "request exceeds size limit");
            return;
        }

        byte[] body;
        try
        {
            body = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
        }
        catch (IOException)
        {
            return;
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(body));
        if (body.Length > MaxRequestBytes)
        {
            Reject(entry, id, hash, "request exceeds size limit");
            return;
        }

        if (!_seen.Add(id))
        {
            Reject(entry, id, hash, "replayed request id", writeResponse: false);
            return;
        }

        if (!TryParse(body, id, out string command, out int timeoutSeconds, out string? error))
        {
            Reject(entry, id, hash, error ?? "invalid request");
            return;
        }

        file.Delete();
        var started = DateTimeOffset.UtcNow;
        RelayCommandResult result;
        try
        {
            result = await execute(command, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = new("error", null, "", false, "executor failure");
        }

        WriteResponse(id, hash, result);
        Processed++;
        log(new JsonObject
        {
            ["time"] = started,
            ["id"] = id,
            ["requestSha256"] = hash,
            ["command"] = command,
            ["timeoutSeconds"] = timeoutSeconds,
            ["status"] = result.Status,
            ["exitCode"] = result.ExitCode,
            ["outputBytes"] = Encoding.UTF8.GetByteCount(result.Output),
            ["truncated"] = result.Truncated,
            ["reason"] = result.Reason,
            ["durationMs"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds
        });
    }

    public static bool TryParse(byte[] body, string expectedId, out string command, out int timeoutSeconds, out string? error)
    {
        command = "";
        timeoutSeconds = 120;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "request is not a JSON object";
                return false;
            }

            bool hasId = false;
            bool hasCommand = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "id" when property.Value.ValueKind == JsonValueKind.String:
                        hasId = property.Value.GetString() == expectedId;
                        break;
                    case "command" when property.Value.ValueKind == JsonValueKind.String:
                        command = property.Value.GetString()!;
                        hasCommand = command.Length is > 0 and <= MaxCommandLength && !command.Contains('\0');
                        break;
                    case "timeoutSeconds" when property.Value.ValueKind == JsonValueKind.Number
                                               && property.Value.TryGetInt32(out timeoutSeconds)
                                               && timeoutSeconds is >= 1 and <= 600:
                        break;
                    default:
                        error = $"invalid property '{property.Name}'";
                        return false;
                }
            }

            error = !hasId ? "request id does not match file name"
                : !hasCommand ? "invalid or missing command"
                : null;
            return error is null;
        }
        catch (JsonException)
        {
            error = "request is not valid JSON";
            return false;
        }
    }

    private void Reject(FileSystemInfo entry, string? id, string? hash, string reason, bool writeResponse = true)
    {
        Rejected++;
        if (entry is DirectoryInfo directory && entry.LinkTarget is null)
        {
            directory.Delete(recursive: true);
        }
        else
        {
            entry.Delete();
        }

        if (id is not null && writeResponse)
        {
            WriteResponse(id, hash, new("rejected", null, "", false, reason));
        }

        log(new JsonObject
        {
            ["time"] = DateTimeOffset.UtcNow,
            ["name"] = entry.Name,
            ["id"] = id,
            ["requestSha256"] = hash,
            ["status"] = "rejected",
            ["reason"] = reason
        });
    }

    private void WriteResponse(string id, string? hash, RelayCommandResult result)
    {
        string final = Path.Combine(responsesDirectory, $"{id}.json");
        if (File.Exists(final))
        {
            return;
        }

        string temporary = Path.Combine(responsesDirectory, $".{id}.tmp");
        File.WriteAllText(temporary, new JsonObject
        {
            ["id"] = id,
            ["requestSha256"] = hash,
            ["status"] = result.Status,
            ["exitCode"] = result.ExitCode,
            ["output"] = result.Output,
            ["truncated"] = result.Truncated,
            ["reason"] = result.Reason
        }.ToJsonString());
        File.Move(temporary, final);
    }
}
