using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Apply;
using TurnRoi.Core.Model;

namespace TurnRoi.Core.Guardrails;

public enum HookPhase { PreToolUse, PostToolUse }

/// <summary>A hook event in agent-neutral form.</summary>
public sealed record HookInput(
    TargetAgent Agent,
    string ToolName,
    string? FilePath,
    string? Range,
    long? ResponseChars,
    string? TranscriptPath,
    string SessionKey,
    string? ProjectDir);

/// <summary>What the hook process should print and return.</summary>
public sealed record HookOutput(string? Stdout, string? Stderr, int ExitCode);

/// <summary>
/// Translates between each agent's hook protocol and <see cref="GuardrailEngine"/>.
/// <list type="bullet">
/// <item>Claude Code: snake_case payload with <c>tool_name</c>, <c>tool_input</c>, <c>tool_response</c>, <c>transcript_path</c>.
/// Feedback: stderr + exit 2 (blocks on PreToolUse).</item>
/// <item>GitHub Copilot: VS Code sends the Claude-style payload; Copilot CLI sends camelCase <c>toolName</c>/<c>toolArgs</c>/<c>toolResult</c>.
/// Feedback the Claude way.</item>
/// <item>Cursor: <c>tool_name</c>, <c>tool_input</c>, <c>tool_output</c>, <c>conversation_id</c>, <c>workspace_roots</c>. Answers are JSON on
/// stdout: preToolUse must always print a permission (invalid output blocks the call), postToolUse may add <c>additional_context</c>.</item>
/// </list>
/// </summary>
public static class HookProtocol
{
    public static HookInput Parse(JsonObject p, TargetAgent? hint)
    {
        var agent = hint ?? (p["cursor_version"] is not null || p["conversation_id"] is not null ? TargetAgent.Cursor : TargetAgent.Claude);

        var toolName = Str(p["tool_name"]) ?? Str(p["toolName"]) ?? "";
        var input = p["tool_input"] as JsonObject ?? ParseObject(p["toolArgs"]) ?? p["toolArgs"] as JsonObject;
        var filePath = Str(input?["file_path"]) ?? Str(input?["filePath"]) ?? Str(input?["path"]) ??
                       Str(input?["target_file"]) ?? Str(input?["notebook_path"]);

        var response = p["tool_response"] ?? p["tool_output"] ?? p["toolResult"];
        long? responseChars = response is null ? null : ResponseChars(response);

        var projectDir = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") is { Length: > 0 } env ? env
            : (p["workspace_roots"] as JsonArray)?.FirstOrDefault() is JsonValue root && root.TryGetValue<string>(out var r) ? r
            : Str(p["cwd"]);

        var sessionKey = Str(p["session_id"]) ?? Str(p["conversation_id"]) ?? Str(p["sessionId"]) ?? "unknown";
        if (filePath is not null && projectDir is not null && !Path.IsPathRooted(filePath))
            filePath = Path.GetFullPath(Path.Combine(projectDir, filePath));

        return new HookInput(agent, toolName, filePath, RangeOf(input), responseChars,
            Str(p["transcript_path"]), sessionKey, projectDir);
    }

    /// <summary>Formats a decision the way the agent expects it.</summary>
    public static HookOutput Format(TargetAgent agent, HookPhase phase, HookDecision decision)
    {
        if (agent == TargetAgent.Cursor)
        {
            if (phase == HookPhase.PreToolUse)
            {
                var o = new JsonObject { ["permission"] = decision.Block ? "deny" : "allow" };
                if (decision.Feedback is not null)
                {
                    o["agent_message"] = decision.Feedback;
                    o["user_message"] = "TurnROI: " + FirstSentence(decision.Feedback);
                }
                return new HookOutput(o.ToJsonString(), null, 0);
            }
            return decision.Feedback is null
                ? new HookOutput("{}", null, 0)
                : new HookOutput(new JsonObject { ["additional_context"] = decision.Feedback }.ToJsonString(), null, 0);
        }

        return decision.Feedback is null ? new HookOutput(null, null, 0) : new HookOutput(null, decision.Feedback, 2);
    }

    /// <summary>What to answer when anything goes wrong: let the agent carry on.</summary>
    public static HookOutput FailOpen(TargetAgent? agent, HookPhase phase) =>
        agent == TargetAgent.Cursor && phase == HookPhase.PreToolUse
            ? new HookOutput("{\"permission\":\"allow\"}", null, 0)
            : new HookOutput(agent == TargetAgent.Cursor ? "{}" : null, null, 0);

    private static string FirstSentence(string s)
    {
        var text = s.StartsWith("TurnROI: ", StringComparison.Ordinal) ? s["TurnROI: ".Length..] : s;
        var dot = text.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 ? text[..(dot + 1)] : text;
    }

    private static JsonObject? ParseObject(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s) || !s.TrimStart().StartsWith('{')) return null;
        try { return JsonNode.Parse(s) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? RangeOf(JsonObject? input)
    {
        if (input is null) return null;
        var parts = new List<string>();
        foreach (var key in new[] { "offset", "limit", "startLine", "endLine", "start_line", "end_line" })
            if (input[key] is JsonValue v && v.TryGetValue<long>(out var n)) parts.Add($"{key}={n}");
        return parts.Count == 0 ? null : string.Join(',', parts);
    }

    private static long ResponseChars(JsonNode resp) => resp switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length,
        JsonObject o when o["stdout"] is JsonValue so && so.TryGetValue<string>(out var stdout) =>
            stdout.Length + (o["stderr"] is JsonValue se && se.TryGetValue<string>(out var stderr) ? stderr.Length : 0),
        JsonObject o when o["file"]?["content"] is JsonValue fc && fc.TryGetValue<string>(out var content) => content.Length,
        _ => resp.ToJsonString().Length,
    };

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// For agents whose transcripts TurnROI can't parse (Cursor, Copilot), the hooks keep their own small record of
/// completed reads per conversation, so the re-read guard works the same way everywhere.
/// </summary>
public sealed class ReadLedger
{
    private readonly string _path;
    private readonly LedgerState _state;

    private ReadLedger(string path, LedgerState state) { _path = path; _state = state; }

    public static ReadLedger Open(string directory, TargetAgent agent, string sessionKey)
    {
        Directory.CreateDirectory(directory);
        var safe = string.Concat(sessionKey.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
        var path = Path.Combine(directory, $"reads-{agent.ToString().ToLowerInvariant()}-{safe}.json");
        LedgerState? state = null;
        try
        {
            if (File.Exists(path)) state = JsonSerializer.Deserialize(File.ReadAllText(path), LedgerJsonContext.Default.LedgerState);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new ReadLedger(path, state ?? new LedgerState());
    }

    /// <summary>Updates the ledger after a tool call completed: reads are recorded, edits forget the file.</summary>
    public void Record(HookInput e, DateTimeOffset at)
    {
        if (e.FilePath is null) return;
        var probe = new ToolCall { Id = "", Name = e.ToolName };
        if (ToolKinds.IsEdit(probe))
            _state.Reads.RemoveAll(r => r.Path == e.FilePath);
        else if (ToolKinds.IsRead(probe))
        {
            _state.Reads.RemoveAll(r => r.Path == e.FilePath && r.Range == e.Range);
            _state.Reads.Add(new LedgerRead { Path = e.FilePath, Range = e.Range, At = at.ToUnixTimeMilliseconds() });
        }
        else return;
        File.WriteAllText(_path, JsonSerializer.Serialize(_state, LedgerJsonContext.Default.LedgerState));
    }

    /// <summary>The ledger as a session the guardrail engine can reason about.</summary>
    public AgentSession AsSession(string sessionKey)
    {
        var s = new AgentSession { SessionId = sessionKey, Source = "hook-ledger" };
        var turn = new Turn { Index = 1, Prompt = "" };
        foreach (var r in _state.Reads)
            turn.Tools.Add(new ToolCall
            {
                Id = $"{r.Path}#{r.Range}", Name = "Read", Target = r.Path, Range = r.Range, ResultChars = 1,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.At),
            });
        s.Turns.Add(turn);
        return s;
    }
}

internal sealed class LedgerState
{
    public List<LedgerRead> Reads { get; set; } = new();
}

internal sealed class LedgerRead
{
    public string Path { get; set; } = "";
    public string? Range { get; set; }
    public long At { get; set; }
}

[JsonSerializable(typeof(LedgerState))]
internal sealed partial class LedgerJsonContext : JsonSerializerContext;
