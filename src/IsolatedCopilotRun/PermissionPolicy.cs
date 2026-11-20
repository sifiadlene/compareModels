using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace IsolatedCopilotRun;

/// <summary>Host-side permission policy: workspace file access and the sandbox run_command tool only.</summary>
public static class PermissionPolicy
{
    public const string Workspace = "/workspace";

    public static (bool Approved, string Reason) Decide(PermissionRequest request) => request switch
    {
        PermissionRequestRead read => CheckPaths("read", read.Path, read.ResolvedPath),
        PermissionRequestWrite write => CheckPaths("write", write.FileName, write.ResolvedPath),
        PermissionRequestMcp { ServerName: "sandbox", ToolName: "run_command" or "sandbox-run_command" } => (true, "sandbox run_command"),
        PermissionRequestMcp mcp => (false, $"MCP tool {mcp.ServerName}/{mcp.ToolName} is not allowlisted"),
        _ => (false, $"permission kind '{request.Kind}' is not allowlisted")
    };

    public static PermissionDecision ToDecision((bool Approved, string Reason) decision) =>
        decision.Approved ? PermissionDecision.ApproveOnce() : PermissionDecision.Reject(decision.Reason);

    private static (bool, string) CheckPaths(string kind, string? path, string? resolvedPath)
    {
        if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(resolvedPath))
        {
            return (false, $"{kind} request has no path");
        }

        foreach (string? candidate in new[] { path, resolvedPath })
        {
            if (!string.IsNullOrEmpty(candidate) && !IsInsideWorkspace(candidate))
            {
                return (false, $"{kind} outside {Workspace}");
            }
        }

        return (true, $"{kind} inside {Workspace}");
    }

    public static bool IsInsideWorkspace(string path)
    {
        if (!path.StartsWith('/') || path.Contains('\0') || path.Contains('\\'))
        {
            return false;
        }

        var segments = new List<string>();
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return segments.Count > 0 && segments[0] == Workspace[1..];
    }
}
