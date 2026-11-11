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
    CopilotCli,
    Codex,
    Cursor,
    OpenCode,
    Otel,
}

/// <summary>A session somewhere on disk: a file, or a record inside a database or export file.</summary>
/// <param name="Location">The file holding it (a transcript, a database, a CSV or an OTLP export).</param>
public sealed record SessionRef(AgentSource Source, string Id, string Location, DateTimeOffset Modified, long? Size = null,
    string? Folder = null, string? Title = null);

/// <summary>Finds sessions on this machine and reads them with the right adapter.</summary>
public static class SessionSources
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>claude, copilot, copilot-cli, codex, cursor, opencode, otel (case and dashes ignored).</summary>
    public static AgentSource? Parse(string text) =>
        Enum.TryParse<AgentSource>(text.Replace("-", "").Replace("_", ""), ignoreCase: true, out var s) ? s : null;

    public static string Label(AgentSource source) => source switch
    {
        AgentSource.CopilotCli => "copilot-cli",
        _ => source.ToString().ToLowerInvariant(),
    };

    /// <summary>The adapter for file-per-session sources.</summary>
    public static IAgentSourceAdapter AdapterFor(AgentSource source) => source switch
    {
        AgentSource.Claude => new ClaudeCodeJsonlAdapter(),
        AgentSource.Copilot => new CopilotVsCodeAdapter(),
        AgentSource.CopilotCli => new CopilotCliAdapter(),
        AgentSource.Codex => new CodexRolloutAdapter(),
        AgentSource.Cursor => new CursorUsageCsvAdapter(),
        AgentSource.Otel => new OtelFileAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(source), $"{source} sessions are read through {nameof(Read)}"),
    };

    /// <summary>Picks a source from the file itself.</summary>
    public static AgentSource Detect(string path)
    {
        if (new CursorUsageCsvAdapter().CanRead(path)) return AgentSource.Cursor;
        if (path.EndsWith(".vscdb", StringComparison.OrdinalIgnoreCase)) return AgentSource.Cursor;
        if (Path.GetFileName(path).Equals("opencode.db", StringComparison.OrdinalIgnoreCase)) return AgentSource.OpenCode;
        if (new OtelFileAdapter().CanRead(path)) return AgentSource.Otel;
        if (new CodexRolloutAdapter().CanRead(path)) return AgentSource.Codex;
        if (new CopilotCliAdapter().CanRead(path)) return AgentSource.CopilotCli;
        if (new CopilotVsCodeAdapter().CanRead(path)) return AgentSource.Copilot;
        return AgentSource.Claude;
    }

    /// <summary>Default places each source keeps its sessions. OTel exports have none: pass the file.</summary>
    public static IReadOnlyList<string> DefaultRoots(AgentSource source) => source switch
    {
        AgentSource.Claude => [ClaudeRoot],
        AgentSource.Copilot => VsCodeUserDirs().ToList(),
        AgentSource.CopilotCli => [CopilotCliRoot],
        AgentSource.Codex => [CodexRoot],
        AgentSource.Cursor => [CursorLocalStore.DefaultUserDir],
        AgentSource.OpenCode => [OpenCodeStore.DefaultRoot],
        _ => [],
    };

    public static string ClaudeRoot =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, "projects")
            : Path.Combine(Home, ".claude", "projects");

    public static string CodexRoot =>
        Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".codex"), "sessions");

    public static string CopilotCliRoot =>
        Path.Combine(Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } dir ? dir : Path.Combine(Home, ".copilot"), "session-state");

    /// <summary>Session files of file-per-session sources, most recently modified first.</summary>
    public static IReadOnlyList<FileInfo> FindSessions(AgentSource source, string? root = null, int limit = 20)
    {
        var roots = root is null ? DefaultRoots(source) : [root];
        var files = source switch
        {
            AgentSource.Claude => roots.SelectMany(r => Enumerate(r, "*.jsonl", SearchOption.AllDirectories)),
            AgentSource.Copilot => roots.SelectMany(CopilotSessionFiles),
            AgentSource.CopilotCli => roots.SelectMany(r => Enumerate(r, "events.jsonl", SearchOption.AllDirectories)),
            AgentSource.Codex => roots.SelectMany(r => Enumerate(r, "rollout-*.jsonl", SearchOption.AllDirectories)),
            _ => [],
        };
        return files.OrderByDescending(f => f.LastWriteTimeUtc).Take(limit).ToList();
    }

    /// <summary>Sessions of any source, most recent first.</summary>
    public static IReadOnlyList<SessionRef> List(AgentSource source, string? root = null, int limit = 20)
    {
        switch (source)
        {
            case AgentSource.Cursor:
            {
                if (root is not null && root.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && File.Exists(root))
                    return [new SessionRef(source, Path.GetFileNameWithoutExtension(root), root, File.GetLastWriteTime(root), new FileInfo(root).Length, Title: "usage export")];
                var store = new CursorLocalStore(root);
                if (!store.Exists) return [];
                return store.List(limit).Select(c => new SessionRef(source, c.Id, store.DatabasePath,
                    c.Updated ?? DateTimeOffset.MinValue, c.Messages, c.Folder, c.Name)).ToList();
            }
            case AgentSource.OpenCode:
            {
                var store = new OpenCodeStore(root);
                if (!store.Exists) return [];
                return store.List(limit).Select(s => new SessionRef(source, s.Id, root ?? OpenCodeStore.DefaultRoot,
                    s.Updated ?? DateTimeOffset.MinValue, null, s.Folder, s.Title)).ToList();
            }
            case AgentSource.Otel:
            {
                if (root is null || !File.Exists(root)) return [];
                return new OtelFileAdapter().List(root).Take(limit).Select(s => new SessionRef(source, s.Id, root,
                    s.Ended ?? s.Started ?? DateTimeOffset.MinValue, s.Turns, s.Folder)).ToList();
            }
            default:
                return FindSessions(source, root, limit).Select(f => new SessionRef(source,
                    source == AgentSource.CopilotCli ? f.Directory?.Name ?? f.Name : Path.GetFileNameWithoutExtension(f.Name),
                    f.FullName, f.LastWriteTime, f.Length, FolderOf(source, f))).ToList();
        }
    }

    /// <summary>Reads a session. For Cursor chats, <paramref name="usageCsv"/> adds billed cost from the dashboard export.</summary>
    public static AgentSession Read(SessionRef r, string? usageCsv = null) => r.Source switch
    {
        AgentSource.Cursor when r.Location.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) => new CursorUsageCsvAdapter().Read(r.Location),
        AgentSource.Cursor => new CursorLocalStore(r.Location).Read(r.Id,
            usageCsv is null ? null : new CursorUsageCsvAdapter().Read(usageCsv).Turns.SelectMany(t => t.Calls).ToList()),
        AgentSource.OpenCode => new OpenCodeStore(r.Location).Read(r.Id),
        AgentSource.Otel => new OtelFileAdapter().Read(r.Location, r.Id),
        _ => AdapterFor(r.Source).Read(r.Location),
    };

    /// <summary>Resolves a file path, a session id (prefix), or "latest".</summary>
    public static SessionRef? Resolve(AgentSource source, string pathOrId, string? root = null)
    {
        if (File.Exists(pathOrId))
        {
            var full = Path.GetFullPath(pathOrId);
            return source switch
            {
                AgentSource.Cursor when full.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) => List(source, full, 1).FirstOrDefault(),
                AgentSource.Cursor or AgentSource.OpenCode or AgentSource.Otel => List(source, full, 1).FirstOrDefault(),
                _ => new SessionRef(source, Path.GetFileNameWithoutExtension(full), full, File.GetLastWriteTime(full), new FileInfo(full).Length),
            };
        }
        if (pathOrId.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return List(source, root, 1).FirstOrDefault();
        return List(source, root, int.MaxValue)
            .FirstOrDefault(s => s.Id.StartsWith(pathOrId, StringComparison.OrdinalIgnoreCase) ||
                                 Path.GetFileName(s.Location).StartsWith(pathOrId, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FolderOf(AgentSource source, FileInfo f) => source switch
    {
        // Copilot: <User>/workspaceStorage/<hash>/chatSessions/<id>.jsonl — the hash folder identifies the workspace.
        AgentSource.Copilot => f.Directory?.Name == "emptyWindowChatSessions" ? "(no folder)" : f.Directory?.Parent?.Name,
        AgentSource.CopilotCli => null,
        _ => f.Directory?.Name,
    };

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
