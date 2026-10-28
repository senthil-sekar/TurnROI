using System.Text.Json;
using System.Text.RegularExpressions;
using TurnRoi.Core.Model;

namespace TurnRoi.Core.Adapters;

/// <summary>
/// Parses OpenAI Codex CLI rollouts (<c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c>, or under <c>$CODEX_HOME</c>).
///
/// Each line is <c>{"timestamp", "type", "payload"}</c>:
/// <list type="bullet">
/// <item><c>session_meta</c>: session <c>id</c> and <c>cwd</c>.</item>
/// <item><c>turn_context</c>: the <c>model</c> (and <c>cwd</c>) in effect for what follows.</item>
/// <item><c>event_msg</c> / <c>user_message</c>: a human prompt, which starts a turn.</item>
/// <item><c>event_msg</c> / <c>token_count</c>: usage; <c>info.total_token_usage</c> is cumulative, so each model call is the
/// difference from the previous total (duplicated events produce no difference and are ignored).
/// <c>input_tokens</c> includes <c>cached_input_tokens</c>.</item>
/// <item><c>response_item</c> / <c>function_call</c>, <c>custom_tool_call</c> and their <c>*_output</c>: tool calls,
/// linked by <c>call_id</c>.</item>
/// </list>
/// The format is internal to Codex; unknown lines are ignored and unparseable lines counted.
/// </summary>
public sealed class CodexRolloutAdapter : IAgentSourceAdapter
{
    public string Name => "codex";

    private static readonly Regex InjectedContext = new(
        @"<(environment_context|user_instructions|user_shell_command)>[\s\S]*?</\1>", RegexOptions.Compiled);

    private static readonly Regex PatchFile = new(@"^\*\*\* (Update|Add|Delete) File: (.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public bool CanRead(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        if (Path.GetFileName(path).StartsWith("rollout-", StringComparison.Ordinal)) return true;
        using var reader = new StreamReader(path);
        var first = reader.ReadLine() ?? "";
        return first.Contains("\"session_meta\"", StringComparison.Ordinal);
    }

    public AgentSession Read(string path) => Parse(File.ReadLines(path), Path.GetFileNameWithoutExtension(path));

    public AgentSession Parse(IEnumerable<string> lines, string fallbackSessionId = "unknown")
    {
        string? sessionId = null, cwd = null, model = null;
        var skipped = 0;
        var turns = new List<Turn>();
        var tools = new Dictionary<string, ToolCall>();
        (long Input, long Cached, long Output)? lastTotal = null;
        var callNo = 0;

        Turn Current(DateTimeOffset? ts)
        {
            if (turns.Count == 0) turns.Add(new Turn { Index = 0, Prompt = "(before first prompt)", StartedAt = ts });
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
                var type = Str(root, "type");
                var ts = Str(root, "timestamp") is { } t && DateTimeOffset.TryParse(t, out var dto) ? dto : (DateTimeOffset?)null;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                var kind = Str(payload, "type");

                switch (type)
                {
                    case "session_meta":
                        sessionId ??= Str(payload, "id");
                        cwd ??= Str(payload, "cwd");
                        break;

                    case "turn_context":
                        model = Str(payload, "model") ?? model;
                        cwd ??= Str(payload, "cwd");
                        break;

                    case "event_msg" when kind == "user_message":
                        var text = InjectedContext.Replace(Str(payload, "message") ?? "", "").Trim();
                        if (text.Length > 0)
                            turns.Add(new Turn { Index = turns.Count == 0 ? 1 : turns[^1].Index + 1, Prompt = text, StartedAt = ts });
                        break;

                    case "event_msg" when kind == "token_count":
                        if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) break;
                        if (info.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object)
                        {
                            var now = (Long(total, "input_tokens"), Long(total, "cached_input_tokens"), Long(total, "output_tokens"));
                            var prev = lastTotal ?? (0, 0, 0);
                            var (dIn, dCached, dOut) = (now.Item1 - prev.Input, now.Item2 - prev.Cached, now.Item3 - prev.Output);
                            lastTotal = now;
                            if (dIn <= 0 && dOut <= 0) break; // Repeated event, or a counter reset.
                            AddCall(Current(ts), ts, Math.Max(0, dIn), Math.Max(0, dCached), Math.Max(0, dOut));
                        }
                        else if (info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
                        {
                            AddCall(Current(ts), ts, Long(last, "input_tokens"), Long(last, "cached_input_tokens"), Long(last, "output_tokens"));
                        }
                        break;

                    case "response_item" when kind is "function_call" or "custom_tool_call" or "local_shell_call":
                    {
                        var id = Str(payload, "call_id") ?? Str(payload, "id") ?? $"call-{tools.Count}";
                        if (tools.ContainsKey(id)) break;
                        var name = Str(payload, "name") ?? (kind == "local_shell_call" ? "local_shell" : "tool");
                        var target = TargetOf(name, payload);
                        // Codex often applies patches through the shell: ["apply_patch", "*** Begin Patch ..."].
                        if (target is not null && target.StartsWith("apply_patch", StringComparison.Ordinal))
                        {
                            name = "apply_patch";
                            target = PatchFile.Match(target) is { Success: true } pm ? pm.Groups[2].Value.Trim() : null;
                        }
                        var call = new ToolCall { Id = id, Name = name, Target = target, Timestamp = ts };
                        tools[id] = call;
                        Current(ts).Tools.Add(call);
                        break;
                    }

                    case "response_item" when kind is "function_call_output" or "custom_tool_call_output" or "local_shell_call_output":
                    {
                        var id = Str(payload, "call_id");
                        if (id is null || !tools.TryGetValue(id, out var call)) break;
                        var (text2, exitCode) = OutputOf(payload);
                        call.ResultChars = (call.ResultChars ?? 0) + text2.Length;
                        call.ResultTail = text2.Length == 0 ? null : text2.Length <= 600 ? text2 : text2[^600..];
                        call.IsError |= exitCode is not null and not 0;
                        break;
                    }
                }
            }
        }

        var session = new AgentSession
        {
            SessionId = sessionId ?? fallbackSessionId,
            Source = Name,
            Cwd = cwd,
            SkippedLines = skipped,
        };
        session.Turns.AddRange(turns);
        return session;

        void AddCall(Turn turn, DateTimeOffset? ts, long input, long cached, long output) =>
            turn.Calls.Add(new LlmCall($"codex-{++callNo}", model ?? "unknown", ts,
                new TokenUsage(Math.Max(0, input - cached), output, 0, cached), false));
    }

    /// <summary>Shell command for shell tools, first file for apply_patch, path-like argument otherwise.</summary>
    private static string? TargetOf(string name, JsonElement payload)
    {
        // custom_tool_call carries raw "input" (e.g. a patch); function_call carries JSON "arguments" as a string.
        if (Str(payload, "input") is { } rawInput)
            return PatchFile.Match(rawInput) is { Success: true } m ? m.Groups[2].Value.Trim() : null;

        JsonElement args = default;
        var hasArgs = false;
        if (Str(payload, "arguments") is { } argText)
        {
            try { args = JsonDocument.Parse(argText).RootElement.Clone(); hasArgs = true; }
            catch (JsonException) { return null; }
        }
        else if (payload.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
        {
            args = action; hasArgs = true;
        }
        if (!hasArgs || args.ValueKind != JsonValueKind.Object) return null;

        if (args.TryGetProperty("command", out var cmd))
        {
            if (cmd.ValueKind == JsonValueKind.String) return Patch(cmd.GetString());
            if (cmd.ValueKind == JsonValueKind.Array)
            {
                var parts = cmd.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToList();
                // ["bash", "-lc", "dotnet test"] → "dotnet test"
                if (parts.Count >= 3 && parts[1] is "-lc" or "-c") return Patch(parts[^1]);
                return Patch(string.Join(' ', parts));
            }
        }
        foreach (var key in new[] { "path", "file_path", "file" })
            if (Str(args, key) is { Length: > 0 } path) return path;
        return null;

        string? Patch(string? command) =>
            command is not null && name.Contains("apply_patch", StringComparison.Ordinal) && PatchFile.Match(command) is { Success: true } m
                ? m.Groups[2].Value.Trim()
                : command;
    }

    private static (string Text, int? ExitCode) OutputOf(JsonElement payload)
    {
        if (!payload.TryGetProperty("output", out var output)) return ("", null);
        if (output.ValueKind == JsonValueKind.String)
        {
            var s = output.GetString() ?? "";
            // Shell outputs are often JSON: {"output":"...","metadata":{"exit_code":1}}
            if (s.StartsWith('{'))
            {
                try
                {
                    using var inner = JsonDocument.Parse(s);
                    var r = inner.RootElement;
                    var text = Str(r, "output") ?? s;
                    int? code = r.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                                meta.TryGetProperty("exit_code", out var ec) && ec.TryGetInt32(out var n) ? n : null;
                    return (text, code);
                }
                catch (JsonException) { }
            }
            var exit = Regex.Match(s, @"(?:Exit code|exit_code)\W+(\d+)");
            return (s, exit.Success ? int.Parse(exit.Groups[1].Value) : null);
        }
        return (output.GetRawText(), null);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
