using System.Text.Json;
using System.Text.RegularExpressions;
using TurnRoi.Core.Model;

namespace TurnRoi.Core.Adapters;

/// <summary>
/// Parses Claude Code session transcripts (~/.claude/projects/&lt;project&gt;/&lt;session&gt;.jsonl).
/// The format is undocumented and changes between versions, so the parser is deliberately tolerant:
/// unknown line types are ignored and malformed lines are counted in <see cref="AgentSession.SkippedLines"/>.
/// </summary>
public sealed class ClaudeCodeJsonlAdapter : IAgentSourceAdapter
{
    public string Name => "claude-code";

    private static readonly Regex InjectedContext = new(@"<system-reminder>[\s\S]*?</system-reminder>", RegexOptions.Compiled);

    private static readonly string[] TargetKeys = ["file_path", "notebook_path", "path", "command", "pattern", "url", "query"];

    public bool CanRead(string path) =>
        path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    public AgentSession Read(string path)
    {
        var fallbackId = Path.GetFileNameWithoutExtension(path);
        return Parse(File.ReadLines(path), fallbackId);
    }

    public AgentSession Parse(IEnumerable<string> lines, string fallbackSessionId = "unknown")
    {
        string? sessionId = null;
        string? cwd = null, branch = null;
        var turns = new List<Turn>();
        var skipped = 0;

        // Streamed assistant responses are written as several lines that share one message id.
        var callIndex = new Dictionary<string, (Turn Turn, int Index)>();
        var toolIndex = new Dictionary<string, ToolCall>();

        Turn CurrentTurn(DateTimeOffset? ts)
        {
            if (turns.Count == 0)
                turns.Add(new Turn { Index = 0, Prompt = "(before first prompt)", StartedAt = ts });
            return turns[^1];
        }

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); }
            catch (JsonException) { skipped++; continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { skipped++; continue; }

                sessionId ??= GetString(root, "sessionId");
                cwd ??= GetString(root, "cwd");
                branch ??= GetString(root, "gitBranch");

                var type = GetString(root, "type");
                if (type is not ("user" or "assistant")) continue;
                if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) continue;
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;

                var ts = GetTimestamp(root);
                var sidechain = root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True;

                if (type == "user")
                    HandleUser(message, ts, sidechain, turns, toolIndex);
                else
                    HandleAssistant(message, ts, sidechain, CurrentTurn(ts), callIndex, toolIndex);
            }
        }

        var session = new AgentSession
        {
            SessionId = sessionId ?? fallbackSessionId,
            Source = Name,
            Cwd = cwd,
            GitBranch = branch,
            SkippedLines = skipped,
        };
        session.Turns.AddRange(turns);
        return session;
    }

    private static void HandleUser(JsonElement message, DateTimeOffset? ts, bool sidechain,
        List<Turn> turns, Dictionary<string, ToolCall> toolIndex)
    {
        if (!message.TryGetProperty("content", out var content)) return;

        string? promptText = null;

        if (content.ValueKind == JsonValueKind.String)
        {
            promptText = content.GetString();
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                var blockType = GetString(block, "type");
                if (blockType == "tool_result")
                {
                    var id = GetString(block, "tool_use_id");
                    if (id is not null && toolIndex.TryGetValue(id, out var tool))
                    {
                        tool.ResultChars = (tool.ResultChars ?? 0) + MeasureResult(block);
                        tool.ResultTail = Tail(ResultText(block));
                        tool.IsError |= block.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
                    }
                }
                else if (blockType == "text" && GetString(block, "text") is { } t)
                {
                    texts.Add(t);
                }
            }
            if (texts.Count > 0) promptText = string.Join("\n", texts);
        }

        // Sub-agent (sidechain) prompts are written by the parent agent, not the human: they do not start a turn.
        if (promptText is null || sidechain) return;

        // The harness prepends injected context (timestamps, reminders) to the human's text; keep only the human's words.
        promptText = InjectedContext.Replace(promptText, "").Trim();
        if (promptText.Length == 0) return;

        turns.Add(new Turn { Index = turns.Count == 0 ? 1 : turns[^1].Index + 1, Prompt = promptText.Trim(), StartedAt = ts });
    }

    private static void HandleAssistant(JsonElement message, DateTimeOffset? ts, bool sidechain, Turn turn,
        Dictionary<string, (Turn Turn, int Index)> callIndex, Dictionary<string, ToolCall> toolIndex)
    {
        var model = GetString(message, "model") ?? "unknown";
        var messageId = GetString(message, "id") ?? Guid.NewGuid().ToString("N");

        if (model != "<synthetic>" && message.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            var usage = new TokenUsage(
                GetLong(u, "input_tokens"),
                GetLong(u, "output_tokens"),
                GetLong(u, "cache_creation_input_tokens"),
                GetLong(u, "cache_read_input_tokens"));

            if (callIndex.TryGetValue(messageId, out var existing))
            {
                var prior = existing.Turn.Calls[existing.Index];
                existing.Turn.Calls[existing.Index] = prior with { Usage = TokenUsage.Max(prior.Usage, usage) };
            }
            else
            {
                turn.Calls.Add(new LlmCall(messageId, model, ts, usage, sidechain));
                callIndex[messageId] = (turn, turn.Calls.Count - 1);
            }
        }

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

        foreach (var block in content.EnumerateArray())
        {
            if (GetString(block, "type") != "tool_use") continue;
            var id = GetString(block, "id");
            if (id is null || toolIndex.ContainsKey(id)) continue;

            var tool = new ToolCall
            {
                Id = id,
                Name = GetString(block, "name") ?? "unknown",
                Target = block.TryGetProperty("input", out var input) ? ExtractTarget(input) : null,
                Range = input.ValueKind == JsonValueKind.Object ? ExtractRange(input) : null,
                Timestamp = ts,
            };
            toolIndex[id] = tool;
            turn.Tools.Add(tool);
        }
    }

    private static string? ExtractTarget(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in TargetKeys)
            if (GetString(input, key) is { Length: > 0 } value) return value;
        return null;
    }

    /// <summary>"offset=…,limit=…" when a read covers only part of a file.</summary>
    internal static string? ExtractRange(JsonElement input)
    {
        var parts = new List<string>();
        foreach (var key in new[] { "offset", "limit" })
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                parts.Add($"{key}={v.GetRawText()}");
        return parts.Count == 0 ? null : string.Join(',', parts);
    }

    private static int MeasureResult(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return 0;
        return c.ValueKind switch
        {
            JsonValueKind.String => c.GetString()?.Length ?? 0,
            JsonValueKind.Array => c.EnumerateArray().Sum(part =>
                GetString(part, "text")?.Length ?? (GetString(part, "type") == "image" ? 0 : part.GetRawText().Length)),
            _ => c.GetRawText().Length,
        };
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        return c.ValueKind switch
        {
            JsonValueKind.String => c.GetString() ?? "",
            JsonValueKind.Array => string.Join("\n", c.EnumerateArray().Select(p => GetString(p, "text")).Where(t => t is not null)),
            _ => "",
        };
    }

    internal static string? Tail(string text, int max = 600) =>
        text.Length == 0 ? null : text.Length <= max ? text : text[^max..];

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static DateTimeOffset? GetTimestamp(JsonElement root) =>
        GetString(root, "timestamp") is { } s && DateTimeOffset.TryParse(s, out var dto) ? dto : null;
}
