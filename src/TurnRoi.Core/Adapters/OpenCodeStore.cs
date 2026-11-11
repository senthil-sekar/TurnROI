using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TurnRoi.Core.Model;
using TurnRoi.Core.Storage;

namespace TurnRoi.Core.Adapters;

/// <summary>
/// Reads OpenCode sessions from <c>~/.local/share/opencode</c> (or <c>$XDG_DATA_HOME/opencode</c>): the SQLite database
/// <c>opencode.db</c> (tables <c>session</c>, <c>message</c>, <c>part</c>, JSON in a <c>data</c> column) used since v1.14, or
/// the older JSON tree <c>storage/session/*/*.json</c>, <c>storage/message/&lt;session&gt;/*.json</c>,
/// <c>storage/part/&lt;message&gt;/*.json</c>.
///
/// Messages carry <c>role</c>, <c>modelID</c>, <c>tokens{input, output, reasoning, cache{read, write}}</c>, <c>cost</c> and
/// <c>time.created</c>; parts carry <c>type</c> (<c>text</c>, <c>tool</c>, …) and, for tools, <c>tool</c>, <c>callID</c> and
/// <c>state{status, input, output, metadata}</c>. Columns and fields are read tolerantly.
/// </summary>
public sealed class OpenCodeStore
{
    public const string SourceName = "opencode";

    private readonly string _root;

    public OpenCodeStore(string? root = null)
    {
        _root = root ?? DefaultRoot;
        if (File.Exists(_root)) _root = Path.GetDirectoryName(_root)!; // pointed at opencode.db
    }

    public static string DefaultRoot
    {
        get
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
                ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "opencode");
        }
    }

    private string DbPath => Path.Combine(_root, "opencode.db");
    private string StorageDir => Path.Combine(_root, "storage");

    public bool Exists => File.Exists(DbPath) || Directory.Exists(Path.Combine(StorageDir, "session"));

    public sealed record SessionInfo(string Id, string? Title, string? Folder, DateTimeOffset? Updated);

    public IReadOnlyList<SessionInfo> List(int limit = int.MaxValue)
    {
        var sessions = new Dictionary<string, SessionInfo>(StringComparer.Ordinal);
        if (File.Exists(DbPath))
        {
            using var db = SqliteReader.Open(DbPath);
            if (db.HasTable("session"))
                foreach (var (_, row) in db.Rows("session"))
                    if (ToSession(Merge(row)) is { } s) sessions[s.Id] = s;
        }
        var dir = Path.Combine(StorageDir, "session");
        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
                if (ReadJson(file) is JsonObject o && ToSession(o) is { } s) sessions.TryAdd(s.Id, s);

        return sessions.Values.OrderByDescending(s => s.Updated ?? DateTimeOffset.MinValue).Take(limit).ToList();
    }

    public AgentSession Read(string sessionId)
    {
        var (messages, parts, info) = Load(sessionId);
        var session = new AgentSession { SessionId = info?.Id ?? sessionId, Source = SourceName, Cwd = info?.Folder };

        var partsByMessage = parts.GroupBy(p => Str(p["messageID"]) ?? "")
            .ToDictionary(g => g.Key, g => g.OrderBy(p => Str(p["id"]), StringComparer.Ordinal).ToList());

        Turn? turn = null;
        foreach (var m in messages.OrderBy(m => Time(m["time"]?["created"]) ?? DateTimeOffset.MinValue).ThenBy(m => Str(m["id"]), StringComparer.Ordinal))
        {
            var id = Str(m["id"]) ?? "";
            var at = Time(m["time"]?["created"]);
            var myParts = partsByMessage.GetValueOrDefault(id) ?? [];
            session.Cwd ??= Str(m["path"]?["cwd"]) ?? Str(m["path"]?["root"]);

            if (Str(m["role"]) == "user")
            {
                var text = string.Join("\n", myParts.Where(p => Str(p["type"]) == "text" && !IsTrue(p["synthetic"]))
                    .Select(p => Str(p["text"])).OfType<string>()).Trim();
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

            var tokens = m["tokens"];
            var usage = new TokenUsage(
                Long(tokens?["input"]),
                Long(tokens?["output"]) + Long(tokens?["reasoning"]),
                Long(tokens?["cache"]?["write"]),
                Long(tokens?["cache"]?["read"]));
            if (usage.Total > 0)
            {
                var model = Str(m["modelID"]) ?? Str(m["model"]?["modelID"]) ?? "unknown";
                decimal? cost = m["cost"] is JsonValue cv && cv.TryGetValue<double>(out var c) && c > 0 ? (decimal)c : null;
                turn.Calls.Add(new LlmCall(id, model, at, usage, false, ReportedCost: cost));
            }

            foreach (var p in myParts.Where(p => Str(p["type"]) == "tool"))
            {
                var state = p["state"];
                var input = state?["input"] as JsonObject;
                string? target = null;
                foreach (var key in new[] { "filePath", "file_path", "path", "command", "pattern", "url" })
                    if (Str(input?[key]) is { Length: > 0 } v) { target = v; break; }
                var range = new List<string>();
                foreach (var key in new[] { "offset", "limit" })
                    if (input?[key] is JsonValue rv && rv.TryGetValue<long>(out var n)) range.Add($"{key}={n}");
                var output = Str(state?["output"]) ?? Str(state?["error"]) ?? "";
                var exit = state?["metadata"]?["exit"] is JsonValue ev && ev.TryGetValue<int>(out var e) ? e : (int?)null;
                turn.Tools.Add(new ToolCall
                {
                    Id = Str(p["callID"]) ?? Str(p["id"]) ?? $"{id}-tool",
                    Name = Str(p["tool"]) ?? "tool",
                    Target = target,
                    Range = range.Count == 0 ? null : string.Join(',', range),
                    Timestamp = Time(state?["time"]?["start"]) ?? at,
                    ResultChars = output.Length == 0 ? null : output.Length,
                    ResultTail = output.Length == 0 ? null : output.Length <= 600 ? output : output[^600..],
                    IsError = Str(state?["status"]) == "error" || exit is not null and not 0,
                });
            }
        }
        return session;
    }

    private (List<JsonObject> Messages, List<JsonObject> Parts, SessionInfo? Info) Load(string sessionId)
    {
        var info = List().FirstOrDefault(s => s.Id == sessionId) ?? List().FirstOrDefault(s => s.Id.StartsWith(sessionId, StringComparison.Ordinal));
        var id = info?.Id ?? sessionId;
        var messages = new List<JsonObject>();
        var parts = new List<JsonObject>();

        if (File.Exists(DbPath))
        {
            using var db = SqliteReader.Open(DbPath);
            if (db.HasTable("message"))
                foreach (var (_, row) in db.Rows("message"))
                {
                    var m = Merge(row);
                    if ((Str(m["sessionID"]) ?? Str(m["session_id"])) == id) messages.Add(m);
                }
            if (db.HasTable("part"))
            {
                var ids = messages.Select(m => Str(m["id"])).OfType<string>().ToHashSet(StringComparer.Ordinal);
                foreach (var (_, row) in db.Rows("part"))
                {
                    var p = Merge(row);
                    if (Str(p["messageID"]) is { } mid && ids.Contains(mid)) parts.Add(p);
                }
            }
        }

        if (messages.Count == 0)
        {
            var msgDir = Path.Combine(StorageDir, "message", id);
            if (Directory.Exists(msgDir))
                foreach (var f in Directory.EnumerateFiles(msgDir, "*.json"))
                    if (ReadJson(f) is JsonObject m)
                    {
                        messages.Add(m);
                        var partDir = Path.Combine(StorageDir, "part", Str(m["id"]) ?? "");
                        if (Directory.Exists(partDir))
                            foreach (var pf in Directory.EnumerateFiles(partDir, "*.json"))
                                if (ReadJson(pf) is JsonObject p) parts.Add(p);
                    }
        }

        if (messages.Count == 0 && info is null) throw new KeyNotFoundException($"no OpenCode session '{sessionId}'");
        return (messages, parts, info);
    }

    /// <summary>A row's JSON <c>data</c> column merged with its plain columns (ids, timestamps), so either layout reads the same.</summary>
    private static JsonObject Merge(Dictionary<string, object?> row)
    {
        var o = row.TryGetValue("data", out var data) && ParseText(data) is JsonObject parsed ? parsed : new JsonObject();
        void Set(string key, object? value)
        {
            if (o[key] is not null || value is null) return;
            o[key] = value switch
            {
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                string s => JsonValue.Create(s),
                _ => null,
            };
        }
        foreach (var (k, v) in row)
        {
            switch (k.ToLowerInvariant())
            {
                case "data": break;
                case "session_id": Set("sessionID", v); break;
                case "message_id": Set("messageID", v); break;
                case "project_id": Set("projectID", v); break;
                default: Set(k, v); break;
            }
        }
        return o;
    }

    private static SessionInfo? ToSession(JsonObject o)
    {
        if (Str(o["id"]) is not { } id) return null;
        var updated = Time(o["time"]?["updated"]) ?? Time(o["time_updated"]) ?? Time(o["time"]?["created"]) ?? Time(o["time_created"]);
        return new SessionInfo(id, Str(o["title"]), Str(o["directory"]) ?? Str(o["path"]?["cwd"]), updated);
    }

    private static JsonNode? ParseText(object? value)
    {
        var text = value switch { byte[] b => Encoding.UTF8.GetString(b), string s => s, _ => null };
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static JsonNode? ReadJson(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsTrue(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static DateTimeOffset? Time(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var ms) || (v.TryGetValue<double>(out var d) && (ms = (long)d) > 0))
            return ms > 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : ms > 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(ms) : null;
        return v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, out var t) ? t : null;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long Long(JsonNode? n) =>
        n is JsonValue v ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : 0 : 0;
}
