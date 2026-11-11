using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TurnRoi.Core.Model;
using TurnRoi.Core.Storage;

namespace TurnRoi.Core.Adapters;

/// <summary>
/// Reads Cursor's chats from its local SQLite database: <c>&lt;Cursor User&gt;/globalStorage/state.vscdb</c>, table
/// <c>cursorDiskKV</c>. Each chat ("composer") is a <c>composerData:&lt;id&gt;</c> JSON value listing its messages
/// ("bubbles") in <c>fullConversationHeadersOnly</c>; each bubble is a <c>bubbleId:&lt;composerId&gt;:&lt;bubbleId&gt;</c> value
/// (type 1 = user, 2 = assistant) with <c>text</c>, <c>createdAt</c>, <c>tokenCount</c> and, for tool calls,
/// <c>toolFormerData</c> (<c>name</c>, <c>params</c>, <c>result</c>, <c>status</c>). Older chats keep bubbles inline in
/// <c>conversation</c>. The format is internal to Cursor and changes between versions; everything is read tolerantly.
///
/// Recent Cursor builds write zero token counts locally, so cost comes from the dashboard usage CSV when one is given:
/// its events are attributed to the turn that was running at the time.
/// </summary>
public sealed class CursorLocalStore
{
    public const string SourceName = "cursor";

    private readonly string _globalDb;
    private readonly string? _userDir;

    public CursorLocalStore(string? root = null)
    {
        root ??= DefaultUserDir;
        if (File.Exists(root))
        {
            _globalDb = root;
            _userDir = Path.GetDirectoryName(Path.GetDirectoryName(root));
        }
        else
        {
            _userDir = root;
            _globalDb = Path.Combine(root, "globalStorage", "state.vscdb");
        }
    }

    public static string DefaultUserDir
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User");
            if (OperatingSystem.IsMacOS())
                return Path.Combine(home, "Library", "Application Support", "Cursor", "User");
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
            return Path.Combine(config, "Cursor", "User");
        }
    }

    public bool Exists => File.Exists(_globalDb);
    public string DatabasePath => _globalDb;

    public sealed record ComposerInfo(string Id, string? Name, DateTimeOffset? Created, DateTimeOffset? Updated, int Messages, string? Folder);

    /// <summary>Chats, most recently updated first.</summary>
    public IReadOnlyList<ComposerInfo> List(int limit = int.MaxValue)
    {
        if (!Exists) return [];
        using var db = SqliteReader.Open(_globalDb);
        if (!db.HasTable("cursorDiskKV")) return [];
        var folders = WorkspaceFolders(db);

        var list = new List<ComposerInfo>();
        foreach (var row in db.RowsWithPrefix("cursorDiskKV", "key", "composerData:"))
        {
            if (Json(row.GetValueOrDefault("value")) is not JsonObject c) continue;
            var id = Str(c["composerId"]) ?? ((string)row["key"]!)["composerData:".Length..];
            var messages = (c["fullConversationHeadersOnly"] as JsonArray)?.Count ?? (c["conversation"] as JsonArray)?.Count ?? 0;
            if (messages == 0) continue;
            list.Add(new ComposerInfo(id, Str(c["name"]), Time(c["createdAt"]), Time(c["lastUpdatedAt"]) ?? Time(c["createdAt"]),
                messages, folders.GetValueOrDefault(id)));
        }
        return list.OrderByDescending(c => c.Updated ?? DateTimeOffset.MinValue).Take(limit).ToList();
    }

    public AgentSession Read(string composerId, IReadOnlyList<LlmCall>? usageEvents = null)
    {
        using var db = SqliteReader.Open(_globalDb);
        var candidates = db.RowsWithPrefix("cursorDiskKV", "key", $"composerData:{composerId}").Take(50).ToList();
        var composerRow = candidates.FirstOrDefault(r => (string)r["key"]! == $"composerData:{composerId}") ?? candidates.FirstOrDefault()
                          ?? throw new KeyNotFoundException($"no Cursor chat '{composerId}'");
        var composer = Json(composerRow["value"]) as JsonObject ?? throw new InvalidDataException("unreadable composer");
        var id = Str(composer["composerId"]) ?? composerId;
        var composerModel = Str(composer["modelConfig"]?["modelName"]);

        // Bubbles: by header order (current format) or inline (older format).
        var bubbles = new List<JsonObject>();
        var skipped = 0;
        if (composer["fullConversationHeadersOnly"] is JsonArray headers)
        {
            var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var row in db.RowsWithPrefix("cursorDiskKV", "key", $"bubbleId:{id}:"))
            {
                var key = (string)row["key"]!;
                if (Json(row.GetValueOrDefault("value")) is JsonObject b) byId[key[(key.LastIndexOf(':') + 1)..]] = b;
                else skipped++;
            }
            foreach (var h in headers.OfType<JsonObject>())
                if (Str(h["bubbleId"]) is { } bid && byId.TryGetValue(bid, out var b)) bubbles.Add(b);
        }
        else if (composer["conversation"] is JsonArray inline)
        {
            bubbles.AddRange(inline.OfType<JsonObject>());
        }

        var session = new AgentSession
        {
            SessionId = id,
            Source = SourceName,
            Cwd = WorkspaceFolders(db).GetValueOrDefault(id),
            SkippedLines = skipped,
        };

        Turn? turn = null;
        var callNo = 0;
        var lastTime = Time(composer["createdAt"]);
        foreach (var b in bubbles)
        {
            var type = Int(b["type"]);
            var at = Time(b["createdAt"]) ?? Time(b["timingInfo"]?["clientStartTime"]) ?? lastTime;
            lastTime = at ?? lastTime;

            if (type == 1)
            {
                var text = (Str(b["text"]) ?? "").Trim();
                if (text.Length == 0) continue;
                turn = new Turn { Index = session.Turns.Count + 1, Prompt = text, StartedAt = at };
                session.Turns.Add(turn);
                continue;
            }

            if (turn is null)
            {
                turn = new Turn { Index = 0, Prompt = "(before first prompt)", StartedAt = at };
                session.Turns.Add(turn);
            }

            var input = Long(b["tokenCount"]?["inputTokens"]);
            var output = Long(b["tokenCount"]?["outputTokens"]);
            if (input + output > 0)
            {
                var model = Str(b["modelInfo"]?["modelName"]) ?? composerModel ?? "unknown";
                turn.Calls.Add(new LlmCall($"{id}-{++callNo}", model, at, new TokenUsage(input, output, 0, 0), false));
            }

            if (b["toolFormerData"] is JsonObject tool && Str(tool["name"]) is { Length: > 0 } toolName)
                turn.Tools.Add(ToTool(tool, toolName, at, $"{id}-tool-{turn.Tools.Count}-{session.Turns.Count}"));
        }

        var hasTokens = session.Turns.Any(t => t.Calls.Count > 0);
        if (usageEvents is { Count: > 0 })
        {
            var attributed = Attribute(session, usageEvents);
            session.Notes.Add($"{attributed} usage event(s) from the Cursor CSV were matched to this chat by time; " +
                              "events from other chats running at the same moment can be mixed in.");
        }
        else if (!hasTokens)
        {
            session.Notes.Add("Cursor stores no token counts locally in recent versions. Add cost with --usage <usage.csv> " +
                              "(export it from the Cursor dashboard).");
        }
        return session;
    }

    /// <summary>Adds usage-CSV events to the turn that was running when each happened.</summary>
    private static int Attribute(AgentSession session, IReadOnlyList<LlmCall> events)
    {
        var turns = session.Turns.Where(t => t.StartedAt is not null).OrderBy(t => t.StartedAt).ToList();
        if (turns.Count == 0) return 0;
        var lastActivity = session.Turns.SelectMany(t => t.Tools).Select(t => t.Timestamp).OfType<DateTimeOffset>()
            .Concat(turns.Select(t => t.StartedAt!.Value)).Max();
        var end = lastActivity + TimeSpan.FromMinutes(10);

        var count = 0;
        foreach (var e in events.Where(e => e.Timestamp is not null).OrderBy(e => e.Timestamp))
        {
            var at = e.Timestamp!.Value;
            if (at < turns[0].StartedAt!.Value || at > end) continue;
            var turn = turns.Last(t => t.StartedAt!.Value <= at);
            turn.Calls.Add(e);
            count++;
        }
        return count;
    }

    private static ToolCall ToTool(JsonObject tool, string name, DateTimeOffset? at, string fallbackId)
    {
        var args = ParseJsonString(tool["params"]) ?? ParseJsonString(tool["rawArgs"]) ?? tool["params"] as JsonObject;
        string? target = null;
        foreach (var key in new[] { "targetFile", "target_file", "relativeWorkspacePath", "file_path", "filePath", "path", "command", "query", "pattern" })
            if (Str(args?[key]) is { Length: > 0 } v) { target = v; break; }

        string? range = null;
        var parts = new List<string>();
        foreach (var key in new[] { "start_line_one_indexed", "end_line_one_indexed_inclusive", "startLine", "endLine", "offset", "limit" })
            if (args?[key] is JsonValue rv && rv.TryGetValue<long>(out var n)) parts.Add($"{key}={n}");
        if (parts.Count > 0) range = string.Join(',', parts);

        var resultText = Str(tool["result"]) ?? tool["result"]?.ToJsonString() ?? "";
        var resultJson = ParseJsonString(tool["result"]);
        var tailSource = Str(resultJson?["output"]) ?? Str(resultJson?["stdout"]) ?? Str(resultJson?["contents"]) ?? resultText;
        var status = Str(tool["status"]) ?? "";
        var failed = status.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                     Str(tool["additionalData"]?["status"])?.Equals("error", StringComparison.OrdinalIgnoreCase) == true ||
                     (resultJson?["exitCode"] is JsonValue ec && ec.TryGetValue<int>(out var code) && code != 0);

        return new ToolCall
        {
            Id = Str(tool["toolCallId"]) ?? fallbackId,
            Name = name,
            Target = target,
            Range = range,
            Timestamp = at,
            ResultChars = resultText.Length == 0 ? null : resultText.Length,
            ResultTail = tailSource.Length == 0 ? null : tailSource.Length <= 600 ? tailSource : tailSource[^600..],
            IsError = failed,
        };
    }

    /// <summary>composerId → workspace folder, from the global composer index (3.0+) and per-workspace chat lists (older).</summary>
    private Dictionary<string, string> WorkspaceFolders(SqliteReader global)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var workspaceFolders = new Dictionary<string, string>(StringComparer.Ordinal);
        var storage = _userDir is null ? null : Path.Combine(_userDir, "workspaceStorage");
        if (storage is not null && Directory.Exists(storage))
        {
            foreach (var dir in Directory.EnumerateDirectories(storage))
            {
                var folder = ReadWorkspaceJson(Path.Combine(dir, "workspace.json"));
                if (folder is null) continue;
                workspaceFolders[Path.GetFileName(dir)] = folder;

                var db = Path.Combine(dir, "state.vscdb");
                if (!File.Exists(db)) continue;
                try
                {
                    using var ws = SqliteReader.Open(db);
                    if (!ws.HasTable("ItemTable")) continue;
                    foreach (var row in ws.RowsWithPrefix("ItemTable", "key", "composer.composerData"))
                        if (Json(row.GetValueOrDefault("value"))?["allComposers"] is JsonArray all)
                            foreach (var c in all.OfType<JsonObject>())
                                if (Str(c["composerId"]) is { } cid) map[cid] = folder;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException) { }
            }
        }

        try
        {
            if (global.HasTable("ItemTable"))
                foreach (var row in global.RowsWithPrefix("ItemTable", "key", "composer.composerHeaders"))
                    if (Json(row.GetValueOrDefault("value"))?["allComposers"] is JsonArray all)
                        foreach (var c in all.OfType<JsonObject>())
                        {
                            if (Str(c["composerId"]) is not { } cid) continue;
                            var ident = c["workspaceIdentifier"];
                            var folder = UriPath(ident?["uri"]) ??
                                         (Str(ident?["id"]) is { } wid ? workspaceFolders.GetValueOrDefault(wid) : null);
                            if (folder is not null) map[cid] = folder;
                        }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or KeyNotFoundException) { }
        return map;
    }

    private static string? ReadWorkspaceJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var node = JsonNode.Parse(File.ReadAllText(path));
            return UriPath(node?["folder"]) ?? UriPath(node?["workspace"]);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private static string? UriPath(JsonNode? uri)
    {
        if (uri is JsonObject o) return Str(o["fsPath"]) ?? UriPath(o["external"]) ?? Str(o["path"]);
        if (Str(uri) is not { } s) return null;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : s;
    }

    private static JsonNode? Json(object? value)
    {
        var text = value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string s => s,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static JsonObject? ParseJsonString(JsonNode? node)
    {
        if (Str(node) is not { } s || !s.TrimStart().StartsWith('{')) return null;
        try { return JsonNode.Parse(s) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? Time(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var ms) || (v.TryGetValue<double>(out var d) && (ms = (long)d) > 0))
            return ms > 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : ms > 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(ms) : null;
        return v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, out var t) ? t : null;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static long Long(JsonNode? n) =>
        n is JsonValue v ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : 0 : 0;
}
