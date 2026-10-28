using TurnRoi.Core.Model;

namespace TurnRoi.Core.Adapters;

/// <summary>
/// Converts one agent's native session format into the normalized <see cref="AgentSession"/> model.
/// New agents (Codex, OpenCode, Copilot, OTel exports) plug in by implementing this interface.
/// </summary>
public interface IAgentSourceAdapter
{
    string Name { get; }
    bool CanRead(string path);
    AgentSession Read(string path);
}

/// <summary>Finds Claude Code transcripts on this machine.</summary>
public static class ClaudeCodeSessionLocator
{
    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, "projects")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>Most recently modified sessions first.</summary>
    public static IReadOnlyList<FileInfo> FindSessions(string? root = null, int limit = 20)
    {
        var dir = new DirectoryInfo(root ?? DefaultRoot);
        if (!dir.Exists) return [];
        return dir.EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(limit)
            .ToList();
    }

    /// <summary>Resolves a file path, a session id, or "latest" to a transcript file.</summary>
    public static FileInfo? Resolve(string pathOrId, string? root = null)
    {
        if (File.Exists(pathOrId)) return new FileInfo(pathOrId);
        if (pathOrId.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return FindSessions(root, 1).FirstOrDefault();

        var dir = new DirectoryInfo(root ?? DefaultRoot);
        return dir.Exists
            ? dir.EnumerateFiles($"{pathOrId}*.jsonl", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }
}
