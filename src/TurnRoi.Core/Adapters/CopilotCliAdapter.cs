using System.Text.Json;
using System.Text.Json.Nodes;
using TurnRoi.Core.Model;

namespace TurnRoi.Core.Adapters;

/// <summary>
/// Parses GitHub Copilot CLI sessions (<c>~/.copilot/session-state/&lt;id&gt;/events.jsonl</c>, or under <c>$COPILOT_HOME</c>).
/// Each line is <c>{"type", "data", "id", "timestamp", "parentId"}</c>:
/// <list type="bullet">
/// <item><c>session.start</c>: <c>sessionId</c>, <c>context.cwd</c>.</item>
/// <item><c>user.message</c>: <c>content</c>, a human prompt (starts a turn).</item>
/// <item><c>assistant.message</c>: one model response, with <c>toolRequests</c> and, in some versions, <c>outputTokens</c>.</item>
/// <item><c>assistant.usage</c> (when present): per-call <c>model</c>, <c>inputTokens</c>, <c>outputTokens</c>,
/// <c>cacheReadTokens</c>, <c>cacheWriteTokens</c>.</item>
/// <item><c>tool.execution_start</c> / <c>tool.execution_complete</c>: <c>toolCallId</c>, <c>toolName</c>, <c>arguments</c>,
/// <c>success</c>, <c>result.content</c>.</item>
/// <item><c>session.shutdown</c>: <c>modelMetrics</c> per model with session totals.</item>
/// </list>
/// Without per-call usage events, a model's session totals are spread over its responses in proportion to their output
/// tokens; the report says so.
/// </summary>
public sealed class CopilotCliAdapter : IAgentSourceAdapter
{
    public string Name => "copilot-cli";

    public bool CanRead(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        if (Path.GetFileName(path) == "events.jsonl") return true;
        using var reader = new StreamReader(path);
        var first = reader.ReadLine() ?? "";
        return first.Contains("\"session.start\"", StringComparison.Ordinal) && first.Contains("\"data\"", StringComparison.Ordinal);
    }

    public AgentSession Read(string path)
    {
        var fallback = Path.GetFileName(path) == "events.jsonl" ? Path.GetFileName(Path.GetDirectoryName(path)) ?? "unknown" : Path.GetFileNameWithoutExtension(path);
        return Parse(File.ReadLines(path), fallback);
    }

    public AgentSession Parse(IEnumerable<string> lines, string fallbackSessionId = "unknown")
    {
        string? sessionId = null, cwd = null, model = null;
        var skipped = 0;
        var turns = new List<Turn>();
        var tools = new Dictionary<string, ToolCall>(StringComparer.Ordinal);
        var usageEvents = 0;
        var responses = new List<(Turn Turn, int Index, string Model, long Output, DateTimeOffset? At)>();
        JsonObject? shutdownMetrics = null;
        var callNo = 0;

        Turn Current(DateTimeOffset? ts)
        {
            if (turns.Count == 0) turns.Add(new Turn { Index = 0, Prompt = "(before first prompt)", StartedAt = ts });
            return turns[^1];
        }

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonObject e;
            try
            {
                if (JsonNode.Parse(raw) is not JsonObject o) { skipped++; continue; }
                e = o;
            }
            catch (JsonException) { skipped++; continue; }

            var type = Str(e["type"]);
            var data = e["data"] as JsonObject;
            var ts = Str(e["timestamp"]) is { } t && DateTimeOffset.TryParse(t, out var dto) ? dto : (DateTimeOffset?)null;
            if (data is null) continue;

            switch (type)
            {
                case "session.start":
                    sessionId ??= Str(data["sessionId"]);
                    cwd ??= Str(data["context"]?["cwd"]) ?? Str(data["cwd"]);
                    model ??= Str(data["selectedModel"]) ?? Str(data["model"]);
                    break;

                case "session.model_change":
                    model = Str(data["newModel"]) ?? Str(data["model"]) ?? Str(data["currentModel"]) ?? model;
                    break;

                case "user.message":
                    var text = (Str(data["content"]) ?? "").Trim();
                    if (text.Length > 0)
                        turns.Add(new Turn { Index = turns.Count == 0 ? 1 : turns[^1].Index + 1, Prompt = text, StartedAt = ts });
                    break;

                case "assistant.usage":
                {
                    usageEvents++;
                    var m = Str(data["model"]) ?? model ?? "unknown";
                    var input = Long(data["inputTokens"]);
                    var cacheRead = Long(data["cacheReadTokens"]);
                    var cacheWrite = Long(data["cacheWriteTokens"]);
                    // OpenAI-style counts include cached tokens in the input; Anthropic-style don't. Subtract only when it fits.
                    var uncached = cacheRead + cacheWrite <= input ? input - cacheRead - cacheWrite : input;
                    Current(ts).Calls.Add(new LlmCall($"cli-{++callNo}", m, ts,
                        new TokenUsage(uncached, Long(data["outputTokens"]), cacheWrite, cacheRead), false,
                        ReportedCost: data["cost"] is JsonValue cv && cv.TryGetValue<double>(out var c) && c > 0 ? (decimal)c : null));
                    break;
                }

                case "assistant.message":
                {
                    var turn = Current(ts);
                    if (Long(data["outputTokens"]) is var output and > 0)
                        responses.Add((turn, turn.Calls.Count, model ?? "unknown", output, ts));
                    if (data["toolRequests"] is JsonArray requests)
                        foreach (var r in requests.OfType<JsonObject>())
                            AddTool(turn, Str(r["toolCallId"]), Str(r["name"]), r["arguments"], ts);
                    break;
                }

                case "tool.execution_start":
                    AddTool(Current(ts), Str(data["toolCallId"]), Str(data["toolName"]), data["arguments"], ts);
                    if (Str(data["model"]) is { } toolModel) model = toolModel;
                    break;

                case "tool.execution_complete":
                {
                    if (Str(data["toolCallId"]) is not { } id || !tools.TryGetValue(id, out var call)) break;
                    var content = Str(data["result"]?["content"]) ?? Str(data["result"]?["detailedContent"]) ?? Str(data["result"]) ??
                                  data["result"]?.ToJsonString() ?? "";
                    call.ResultChars = (call.ResultChars ?? 0) + content.Length;
                    call.ResultTail = content.Length == 0 ? null : content.Length <= 600 ? content : content[^600..];
                    call.IsError |= data["success"] is JsonValue sv && sv.TryGetValue<bool>(out var ok) && !ok;
                    if (Str(data["model"]) is { } m2) model = m2;
                    break;
                }

                case "session.shutdown":
                    shutdownMetrics = data["modelMetrics"] as JsonObject;
                    break;
            }
        }

        var session = new AgentSession { SessionId = sessionId ?? fallbackSessionId, Source = Name, Cwd = cwd, SkippedLines = skipped };
        session.Turns.AddRange(turns);

        if (usageEvents == 0)
        {
            if (shutdownMetrics is not null && shutdownMetrics.Count > 0)
            {
                SpreadSessionTotals(responses, shutdownMetrics, ref callNo);
                session.Notes.Add("Copilot CLI recorded only session totals: each model's tokens were spread over its responses by output size.");
            }
            else
            {
                foreach (var (turn, _, m, output, at) in responses)
                    turn.Calls.Add(new LlmCall($"cli-{++callNo}", m, at, new TokenUsage(0, output, 0, 0), false));
                if (responses.Count > 0)
                    session.Notes.Add("This Copilot CLI session has no usage totals yet (still running or ended abruptly): only output tokens are counted.");
            }
        }
        session.Notes.Add("Copilot bills premium requests, not tokens: cost shown is the API-equivalent price for comparison.");
        return session;

        void AddTool(Turn turn, string? id, string? name, JsonNode? args, DateTimeOffset? at)
        {
            if (id is null || name is null || tools.ContainsKey(id)) return;
            var a = args as JsonObject ?? (Str(args) is { } s && s.TrimStart().StartsWith('{') ? TryParse(s) : null);
            string? target = null;
            foreach (var key in new[] { "path", "file_path", "filePath", "command", "pattern", "url" })
                if (Str(a?[key]) is { Length: > 0 } v) { target = v; break; }
            string? range = a?["view_range"] is JsonArray vr && vr.Count == 2 ? $"view_range={vr[0]}-{vr[1]}" : null;
            var call = new ToolCall { Id = id, Name = name, Target = target, Range = range, Timestamp = at };
            tools[id] = call;
            turn.Tools.Add(call);
        }
    }

    private static void SpreadSessionTotals(
        List<(Turn Turn, int Index, string Model, long Output, DateTimeOffset? At)> responses, JsonObject metrics, ref int callNo)
    {
        foreach (var (modelName, node) in metrics)
        {
            var usage = node?["usage"];
            if (usage is null) continue;
            var totals = (In: Long(usage["inputTokens"]), Out: Long(usage["outputTokens"]), Read: Long(usage["cacheReadTokens"]), Write: Long(usage["cacheWriteTokens"]));
            var uncached = totals.Read + totals.Write <= totals.In ? totals.In - totals.Read - totals.Write : totals.In;
            decimal? cost = node?["requests"]?["cost"] is JsonValue cv && cv.TryGetValue<double>(out var c) && c > 0 ? (decimal)c : null;

            var mine = responses.Where(r => r.Model == modelName).ToList();
            if (mine.Count == 0) mine = responses.Where(r => r.Model == "unknown").ToList();
            if (mine.Count == 0)
            {
                // No responses to spread over: attach the totals to the last turn.
                if (responses.Count == 0) continue;
                mine = [responses[^1]];
            }

            var outSum = Math.Max(1, mine.Sum(r => r.Output));
            foreach (var r in mine)
            {
                var share = (double)r.Output / outSum;
                r.Turn.Calls.Add(new LlmCall($"cli-{++callNo}", modelName, r.At,
                    new TokenUsage((long)(uncached * share), r.Output, (long)(totals.Write * share), (long)(totals.Read * share)), false,
                    ReportedCost: cost is { } total ? total * (decimal)share : null));
            }
        }
    }

    private static JsonObject? TryParse(string s)
    {
        try { return JsonNode.Parse(s) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long Long(JsonNode? n) =>
        n is JsonValue v ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : 0 : 0;
}
