using TurnRoi.Core.Model;

namespace TurnRoi.Core.Adapters;

/// <summary>
/// Converts one agent's native session format into the normalized <see cref="AgentSession"/> model.
/// New agents (Codex, OpenCode, Copilot CLI, OTel exports) plug in by implementing this interface.
/// </summary>
public interface IAgentSourceAdapter
{
    string Name { get; }
    bool CanRead(string path);
    AgentSession Read(string path);
}

public enum AgentSource
{
    Claude,
    Copilot,
    Cursor,
}

/// <summary>Finds session files on this machine and picks the adapter that reads them.</summary>
public static class SessionSources
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static IAgentSourceAdapter AdapterFor(AgentSource source) => source switch
    {
        AgentSource.Claude => new ClaudeCodeJsonlAdapter(),
        AgentSource.Copilot => new CopilotVsCodeAdapter(),
        AgentSource.Cursor => new CursorUsageCsvAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>Picks a source from the file itself: Cursor CSV, Copilot chat session, else Claude Code transcript.</summary>
    public static AgentSource Detect(string path)
    {
        if (new CursorUsageCsvAdapter().CanRead(path)) return AgentSource.Cursor;
        if (new CopilotVsCodeAdapter().CanRead(path)) return AgentSource.Copilot;
        return AgentSource.Claude;
    }

    /// <summary>Default directories each source keeps its sessions in. Cursor has none: its data comes from a CSV export.</summary>
    public static IReadOnlyList<string> DefaultRoots(AgentSource source) => source switch
    {
        AgentSource.Claude => [ClaudeRoot],
        AgentSource.Copilot => VsCodeUserDirs().ToList(),
        _ => [],
    };

    public static string ClaudeRoot =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, "projects")
            : Path.Combine(Home, ".claude", "projects");

    /// <summary>Most recently modified sessions first.</summary>
    public static IReadOnlyList<FileInfo> FindSessions(AgentSource source, string? root = null, int limit = 20)
    {
        var roots = root is null ? DefaultRoots(source) : [root];
        var files = source switch
        {
            AgentSource.Claude => roots.SelectMany(r => Enumerate(r, "*.jsonl", SearchOption.AllDirectories)),
            AgentSource.Copilot => roots.SelectMany(CopilotSessionFiles),
            _ => [],
        };
        return files.OrderByDescending(f => f.LastWriteTimeUtc).Take(limit).ToList();
    }

    /// <summary>Resolves a file path, a session id (prefix), or "latest" to a session file.</summary>
    public static FileInfo? Resolve(AgentSource source, string pathOrId, string? root = null)
    {
        if (File.Exists(pathOrId)) return new FileInfo(pathOrId);
        if (pathOrId.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return FindSessions(source, root, 1).FirstOrDefault();
        return FindSessions(source, root, int.MaxValue)
            .FirstOrDefault(f => f.Name.StartsWith(pathOrId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>VS Code "User" folders for stable, Insiders and VSCodium on this OS.</summary>
    private static IEnumerable<string> VsCodeUserDirs()
    {
        string baseDir;
        if (OperatingSystem.IsWindows())
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        else if (OperatingSystem.IsMacOS())
            baseDir = Path.Combine(Home, "Library", "Application Support");
        else
            baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Home, ".config");

        foreach (var edition in new[] { "Code", "Code - Insiders", "VSCodium" })
        {
            var dir = Path.Combine(baseDir, edition, "User");
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    /// <summary>
    /// Chat session files under a VS Code User folder (or any folder containing them).
    /// When both id.json (old snapshot) and id.jsonl (current log) exist, the .jsonl is the live copy.
    /// VS Code can also copy an empty-window session into workspace storage, so the same session id may appear
    /// twice; it is counted once (most recently written copy) to avoid double-counting spend.
    /// </summary>
    private static IEnumerable<FileInfo> CopilotSessionFiles(string root)
    {
        var candidates = Enumerate(root, "*.json*", SearchOption.AllDirectories)
            .Where(f => f.Directory?.Name is "chatSessions" or "emptyWindowChatSessions")
            .Where(f => f.Extension is ".json" or ".jsonl");

        return candidates
            .GroupBy(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(f => f.Extension == ".jsonl")
                .ThenBy(f => f.Directory?.Name == "emptyWindowChatSessions") // the workspace copy knows its folder
                .ThenByDescending(f => f.LastWriteTimeUtc)
                .First());
    }

    private static IEnumerable<FileInfo> Enumerate(string root, string pattern, SearchOption option)
    {
        var dir = new DirectoryInfo(root);
        if (!dir.Exists) return [];
        try
        {
            return dir.EnumerateFiles(pattern, new EnumerationOptions
            {
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }
}
