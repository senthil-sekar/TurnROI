using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TurnRoi.Core.Model;

namespace TurnRoi.Core.Adapters;

/// <summary>
/// Reads OpenTelemetry exports in OTLP JSON (one export request per line, as the Collector's file exporter writes them,
/// or a single JSON document). Two shapes are understood:
/// <list type="bullet">
/// <item><b>GenAI spans</b> (OpenTelemetry semantic conventions): <c>chat</c>/<c>text_completion</c>/<c>generate_content</c>
/// spans give model calls (<c>gen_ai.usage.input_tokens</c>, which includes cached tokens, <c>gen_ai.usage.output_tokens</c>,
/// <c>gen_ai.usage.cache_read.input_tokens</c>, <c>gen_ai.usage.cache_creation.input_tokens</c>, <c>gen_ai.response.model</c>);
/// <c>execute_tool</c> spans give tool calls (<c>gen_ai.tool.name</c>). Sessions are grouped by <c>gen_ai.conversation.id</c>
/// (else <c>session.id</c>, else trace), turns by trace.</item>
/// <item><b>Claude Code log events</b>: <c>user_prompt</c> starts a turn; <c>api_request</c> gives a model call
/// (<c>input_tokens</c>, <c>output_tokens</c>, <c>cache_read_tokens</c>, <c>cache_creation_tokens</c>, <c>cost_usd</c>);
/// <c>tool_result</c> gives a tool call. Sessions are grouped by <c>session.id</c>.</item>
/// </list>
/// One file can hold many sessions; <see cref="List"/> enumerates them and <see cref="Read(string, string?)"/> picks one.
/// </summary>
public sealed class OtelFileAdapter : IAgentSourceAdapter
{
    public string Name => "otel";

    private static readonly HashSet<string> ChatOperations = new(StringComparer.Ordinal)
        { "chat", "text_completion", "generate_content" };

    public bool CanRead(string path)
    {
        if (!File.Exists(path)) return false;
        using var reader = new StreamReader(path);
        var buffer = new char[2048];
        var head = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
        return head.Contains("\"resourceSpans\"", StringComparison.Ordinal) || head.Contains("\"resourceLogs\"", StringComparison.Ordinal);
    }

    public AgentSession Read(string path) => Read(path, null);

    public sealed record SessionInfo(string Id, DateTimeOffset? Started, DateTimeOffset? Ended, int Turns, string? Folder);

    public IReadOnlyList<SessionInfo> List(string path) =>
        Parse(path).Values
            .Select(s => new SessionInfo(s.SessionId,
                s.Turns.Select(t => t.StartedAt).OfType<DateTimeOffset>().DefaultIfEmpty().Min() is var a && a != default ? a : null,
                s.Turns.SelectMany(t => t.Calls).Select(c => c.Timestamp).OfType<DateTimeOffset>().DefaultIfEmpty().Max() is var b && b != default ? b : null,
                s.Turns.Count(t => t.Index > 0), s.Cwd))
            .OrderByDescending(s => s.Ended ?? s.Started ?? DateTimeOffset.MinValue)
            .ToList();

    /// <summary>The session with this id (or id prefix), or the most recent one when null.</summary>
    public AgentSession Read(string path, string? sessionId)
    {
        var sessions = Parse(path);
        if (sessions.Count == 0)
            return new AgentSession { SessionId = Path.GetFileNameWithoutExtension(path), Source = Name };
        if (sessionId is not null)
            return sessions.Values.FirstOrDefault(s => s.SessionId == sessionId)
                   ?? sessions.Values.FirstOrDefault(s => s.SessionId.StartsWith(sessionId, StringComparison.Ordinal))
                   ?? throw new KeyNotFoundException($"no session '{sessionId}' in {path}");
        var latest = List(path)[0].Id;
        return sessions[latest];
    }

    private sealed record Item(DateTimeOffset At, string Session, string? Turn, string Kind, Dictionary<string, JsonNode?> Attrs, string? Name);

    private Dictionary<string, AgentSession> Parse(string path)
    {
        var items = new List<Item>();
        var skipped = 0;
        foreach (var doc in Documents(path, ref skipped))
        {
            foreach (var rs in (doc["resourceSpans"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var resource = Attributes(rs["resource"]?["attributes"]);
                foreach (var ss in (rs["scopeSpans"] as JsonArray ?? []).OfType<JsonObject>())
                foreach (var span in (ss["spans"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var a = Attributes(span["attributes"]);
                    foreach (var (k, v) in resource) a.TryAdd(k, v);
                    var op = Str(a.GetValueOrDefault("gen_ai.operation.name"));
                    if (op is null) continue;
                    var trace = Str(span["traceId"]);
                    var session = Str(a.GetValueOrDefault("gen_ai.conversation.id")) ?? Str(a.GetValueOrDefault("session.id")) ?? trace ?? "unknown";
                    var kind = op == "execute_tool" ? "tool" : ChatOperations.Contains(op) ? "call" : op == "invoke_agent" ? "agent" : null;
                    if (kind is null) continue;
                    items.Add(new Item(Nanos(span["startTimeUnixNano"]) ?? DateTimeOffset.MinValue, session, trace, kind, a, Str(span["name"])));
                }
            }

            foreach (var rl in (doc["resourceLogs"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var resource = Attributes(rl["resource"]?["attributes"]);
                foreach (var sl in (rl["scopeLogs"] as JsonArray ?? []).OfType<JsonObject>())
                foreach (var log in (sl["logRecords"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var a = Attributes(log["attributes"]);
                    foreach (var (k, v) in resource) a.TryAdd(k, v);
                    var name = Str(a.GetValueOrDefault("event.name")) ?? Str(log["body"]?["stringValue"]) ?? "";
                    if (name.StartsWith("claude_code.", StringComparison.Ordinal)) name = name["claude_code.".Length..];
                    var kind = name switch { "user_prompt" => "prompt", "api_request" => "call", "tool_result" => "tool", _ => null };
                    if (kind is null) continue;
                    var at = (Str(a.GetValueOrDefault("event.timestamp")) is { } iso && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTimeOffset?)null)
                             ?? Nanos(log["timeUnixNano"]) ?? Nanos(log["observedTimeUnixNano"]) ?? DateTimeOffset.MinValue;
                    var session = Str(a.GetValueOrDefault("session.id")) ?? "unknown";
                    items.Add(new Item(at, session, Str(a.GetValueOrDefault("prompt.id")), kind, a, name));
                }
            }
        }

        var sessions = new Dictionary<string, AgentSession>(StringComparer.Ordinal);
        foreach (var group in items.GroupBy(i => i.Session))
        {
            var s = new AgentSession { SessionId = group.Key, Source = Name, SkippedLines = skipped };
            var ordered = group.OrderBy(i => i.At).ToList();
            var hasPrompts = ordered.Any(i => i.Kind == "prompt");
            var turnsByTrace = new Dictionary<string, Turn>(StringComparer.Ordinal);
            Turn? current = null;
            var n = 0;

            foreach (var i in ordered)
            {
                s.Cwd ??= Str(i.Attrs.GetValueOrDefault("process.cwd")) ?? Str(i.Attrs.GetValueOrDefault("cwd"));
                Turn TurnFor()
                {
                    if (hasPrompts)
                    {
                        if (current is null) { current = new Turn { Index = 0, Prompt = "(before first prompt)", StartedAt = i.At }; s.Turns.Add(current); }
                        return current;
                    }
                    var key = i.Turn ?? "default";
                    if (!turnsByTrace.TryGetValue(key, out var t))
                    {
                        t = new Turn { Index = s.Turns.Count + 1, Prompt = PromptOf(i) ?? i.Name ?? "agent run", StartedAt = i.At };
                        turnsByTrace[key] = t;
                        s.Turns.Add(t);
                    }
                    return t;
                }

                switch (i.Kind)
                {
                    case "prompt":
                        var text = Str(i.Attrs.GetValueOrDefault("prompt"));
                        var length = Long(i.Attrs.GetValueOrDefault("prompt_length"));
                        current = new Turn
                        {
                            Index = s.Turns.Count(t => t.Index > 0) + 1,
                            Prompt = text ?? $"(prompt not logged, {length} chars)",
                            StartedAt = i.At,
                        };
                        s.Turns.Add(current);
                        break;

                    case "agent":
                        TurnFor();
                        break;

                    case "call":
                    {
                        var turn = TurnFor();
                        var a = i.Attrs;
                        TokenUsage usage;
                        if (a.ContainsKey("gen_ai.usage.input_tokens") || a.ContainsKey("gen_ai.usage.output_tokens"))
                        {
                            var input = Long(a.GetValueOrDefault("gen_ai.usage.input_tokens"));
                            var read = Long(a.GetValueOrDefault("gen_ai.usage.cache_read.input_tokens"));
                            var write = Long(a.GetValueOrDefault("gen_ai.usage.cache_creation.input_tokens"));
                            usage = new TokenUsage(Math.Max(0, input - read - write), Long(a.GetValueOrDefault("gen_ai.usage.output_tokens")), write, read);
                        }
                        else
                        {
                            usage = new TokenUsage(Long(a.GetValueOrDefault("input_tokens")), Long(a.GetValueOrDefault("output_tokens")),
                                Long(a.GetValueOrDefault("cache_creation_tokens")), Long(a.GetValueOrDefault("cache_read_tokens")));
                        }
                        var model = Str(a.GetValueOrDefault("gen_ai.response.model")) ?? Str(a.GetValueOrDefault("gen_ai.request.model")) ??
                                    Str(a.GetValueOrDefault("model")) ?? "unknown";
                        decimal? cost = a.GetValueOrDefault("cost_usd") is { } c && Double(c) is { } d ? (decimal)d : null;
                        turn.Calls.Add(new LlmCall($"otel-{++n}", model, i.At, usage, false, ReportedCost: cost));
                        break;
                    }

                    case "tool":
                    {
                        var turn = TurnFor();
                        var a = i.Attrs;
                        var name = Str(a.GetValueOrDefault("gen_ai.tool.name")) ?? Str(a.GetValueOrDefault("tool_name")) ?? Str(a.GetValueOrDefault("name")) ?? "tool";
                        var args = ParseObject(a.GetValueOrDefault("gen_ai.tool.call.arguments")) ?? ParseObject(a.GetValueOrDefault("tool_parameters"));
                        string? target = null;
                        foreach (var key in new[] { "file_path", "filePath", "path", "command", "pattern" })
                            if (Str(args?[key]) is { Length: > 0 } v) { target = v; break; }
                        var success = Str(a.GetValueOrDefault("success"));
                        var size = Long(a.GetValueOrDefault("tool_result_size_bytes"));
                        turn.Tools.Add(new ToolCall
                        {
                            Id = Str(a.GetValueOrDefault("gen_ai.tool.call.id")) ?? $"otel-tool-{++n}",
                            Name = name,
                            Target = target,
                            Timestamp = i.At,
                            ResultChars = size > 0 ? (int)Math.Min(size, int.MaxValue) : null,
                            IsError = success == "false" || a.ContainsKey("error") || a.ContainsKey("error.type"),
                        });
                        break;
                    }
                }
            }

            if (!hasPrompts) s.Notes.Add("No prompt events in this export: turns are agent runs (one per trace).");
            if (s.Turns.Any(t => t.Tools.Count > 0) && s.Turns.SelectMany(t => t.Tools).All(t => t.Target is null))
                s.Notes.Add("Tool parameters aren't in this export, so re-read detection is off.");
            sessions[group.Key] = s;
        }
        return sessions;
    }

    private static IEnumerable<JsonObject> Documents(string path, ref int skipped)
    {
        var docs = new List<JsonObject>();
        var text = File.ReadAllText(path);
        try
        {
            if (JsonNode.Parse(text) is JsonObject whole) { docs.Add(whole); return docs; }
        }
        catch (JsonException) { }

        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject o) docs.Add(o);
                else skipped++;
            }
            catch (JsonException) { skipped++; }
        }
        return docs;
    }

    private static Dictionary<string, JsonNode?> Attributes(JsonNode? list)
    {
        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var kv in (list as JsonArray ?? []).OfType<JsonObject>())
            if (Str(kv["key"]) is { } key) map[key] = Value(kv["value"]);
        return map;
    }

    /// <summary>OTLP AnyValue → plain JSON value.</summary>
    private static JsonNode? Value(JsonNode? any)
    {
        if (any is not JsonObject o) return null;
        if (o["stringValue"] is { } s) return s.DeepClone();
        if (o["intValue"] is { } i) return i.DeepClone();
        if (o["doubleValue"] is { } d) return d.DeepClone();
        if (o["boolValue"] is { } b) return b.DeepClone();
        return null;
    }

    private static string? PromptOf(Item i)
    {
        if (Str(i.Attrs.GetValueOrDefault("gen_ai.input.messages")) is not { } json) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray messages) return null;
            var user = messages.OfType<JsonObject>().LastOrDefault(m => Str(m["role"]) == "user");
            return Str(user?["content"]) ?? (user?["parts"] as JsonArray)?.OfType<JsonObject>().Select(p => Str(p["content"])).FirstOrDefault(c => c is not null);
        }
        catch (JsonException) { return null; }
    }

    private static JsonObject? ParseObject(JsonNode? n)
    {
        if (Str(n) is not { } s || !s.TrimStart().StartsWith('{')) return null;
        try { return JsonNode.Parse(s) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? Nanos(JsonNode? n)
    {
        var text = n is JsonValue v ? v.TryGetValue<string>(out var s) ? s : v.TryGetValue<long>(out var l) ? l.ToString(CultureInfo.InvariantCulture) : null : null;
        return text is not null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ns) && ns > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000)
            : null;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n is JsonValue v2 && v2.TryGetValue<bool>(out var b) ? (b ? "true" : "false") : null;

    private static long Long(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    private static double? Double(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null;
    }
}
