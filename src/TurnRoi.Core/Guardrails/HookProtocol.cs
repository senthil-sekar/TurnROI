using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Apply;
using TurnRoi.Core.Model;

namespace TurnRoi.Core.Guardrails;

public enum HookPhase { PreToolUse, PostToolUse, UserPrompt }

/// <summary>A hook event in agent-neutral form.</summary>
public sealed record HookInput(
    TargetAgent Agent,
    string ToolName,
    string? FilePath,
    string? Range,
    long? ResponseChars,
    string? TranscriptPath,
    string SessionKey,
    string? ProjectDir,
    string? ToolCallId = null,
    string? TurnKey = null,
    string? Model = null);

/// <summary>What the hook process should print and return.</summary>
public sealed record HookOutput(string? Stdout, string? Stderr, int ExitCode);

/// <summary>
/// Translates between each agent's hook protocol and <see cref="GuardrailEngine"/>.
/// <list type="bullet">
/// <item>Claude Code: snake_case payload with <c>tool_name</c>, <c>tool_input</c>, <c>tool_response</c>, <c>transcript_path</c>.
/// Answer: stderr + exit 2 (blocks on PreToolUse; shown to the model on PostToolUse).</item>
/// <item>GitHub Copilot (VS Code, CLI, cloud agent share one harness): PascalCase event names select the snake_case,
/// Claude-compatible payload; camelCase names get <c>toolName</c>/<c>toolArgs</c>/<c>toolResult</c>. Answer: JSON on stdout
/// carrying both the Copilot shape (top-level <c>permissionDecision</c>, <c>additionalContext</c>) and the Claude shape
/// (<c>hookSpecificOutput</c>), exit 0.</item>
/// <item>Cursor: <c>tool_name</c>, <c>tool_input</c>, <c>tool_output</c>, <c>conversation_id</c>, <c>generation_id</c>,
/// <c>workspace_roots</c>. Answer: JSON on stdout; a permission hook must always print a permission (invalid output blocks).</item>
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
                       Str(input?["target_file"]) ?? Str(input?["targetFile"]) ?? Str(input?["notebook_path"]);

        var response = p["tool_response"] ?? p["tool_output"] ?? p["toolResult"];
        long? responseChars = response is null ? null : ResponseChars(response);

        var projectDir = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") is { Length: > 0 } env ? env
            : (p["workspace_roots"] as JsonArray)?.FirstOrDefault() is JsonValue root && root.TryGetValue<string>(out var r) ? r
            : Str(p["cwd"]);

        var sessionKey = Str(p["session_id"]) ?? Str(p["conversation_id"]) ?? Str(p["sessionId"]) ?? "unknown";
        if (filePath is not null && projectDir is not null && !Path.IsPathRooted(filePath))
            filePath = Path.GetFullPath(Path.Combine(projectDir, filePath));

        var toolCallId = Str(p["tool_use_id"]) ?? Str(p["toolCallId"]) ?? Str(p["tool_call_id"]);
        var turnKey = Str(p["generation_id"]) ?? Str(p["prompt_id"]);
        var model = Str(p["model"]) ?? Str(p["model_name"]);

        return new HookInput(agent, toolName, filePath, RangeOf(input), responseChars,
            Str(p["transcript_path"]), sessionKey, projectDir, toolCallId, turnKey, model);
    }

    /// <summary>Formats a decision the way the agent expects it.</summary>
    public static HookOutput Format(TargetAgent agent, HookPhase phase, HookDecision decision)
    {
        switch (agent)
        {
            case TargetAgent.Cursor:
                if (phase == HookPhase.UserPrompt) return new HookOutput("{\"continue\":true}", null, 0);
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

            case TargetAgent.Copilot:
                if (phase == HookPhase.UserPrompt || decision.Feedback is null) return new HookOutput("{}", null, 0);
                if (phase == HookPhase.PreToolUse)
                {
                    var o = new JsonObject
                    {
                        ["permissionDecision"] = decision.Block ? "deny" : "allow",
                        ["permissionDecisionReason"] = decision.Feedback,
                        ["hookSpecificOutput"] = new JsonObject
                        {
                            ["hookEventName"] = "PreToolUse",
                            ["permissionDecision"] = decision.Block ? "deny" : "allow",
                            ["permissionDecisionReason"] = decision.Feedback,
                        },
                    };
                    return new HookOutput(o.ToJsonString(), null, 0);
                }
                return new HookOutput(new JsonObject
                {
                    ["additionalContext"] = decision.Feedback,
                    ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PostToolUse", ["additionalContext"] = decision.Feedback },
                }.ToJsonString(), null, 0);

            default:
                return decision.Feedback is null || phase == HookPhase.UserPrompt
                    ? new HookOutput(null, null, 0)
                    : new HookOutput(null, decision.Feedback, 2);
        }
    }

    /// <summary>What to answer when anything goes wrong: let the agent carry on.</summary>
    public static HookOutput FailOpen(TargetAgent? agent, HookPhase phase) => agent switch
    {
        TargetAgent.Cursor when phase == HookPhase.PreToolUse => new HookOutput("{\"permission\":\"allow\"}", null, 0),
        TargetAgent.Cursor when phase == HookPhase.UserPrompt => new HookOutput("{\"continue\":true}", null, 0),
        TargetAgent.Cursor or TargetAgent.Copilot => new HookOutput("{}", null, 0),
        _ => new HookOutput(null, null, 0),
    };

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
        JsonObject o when o["textResultForLlm"] is JsonValue tr && tr.TryGetValue<string>(out var text) => text.Length,
        _ => resp.ToJsonString().Length,
    };

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// For agents whose transcripts the hooks can't use (Cursor, and Copilot without a chat session file), the hooks keep
/// their own small record per conversation: completed reads (for the re-read guard) and tool calls in the current turn
/// (for the tool-call budget). A turn starts at a prompt hook, or when Cursor's <c>generation_id</c> changes.
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

    public int Turn => _state.Turn;

    /// <summary>Starts a new turn (a prompt was submitted).</summary>
    public void NewTurn()
    {
        _state.Turn++;
        _state.TurnToolCalls = 0;
        Save();
    }

    /// <summary>Starts a new turn when the agent's turn id changes (Cursor's generation_id).</summary>
    public void ObserveTurnKey(string? turnKey)
    {
        if (turnKey is null || turnKey == _state.TurnKey) return;
        _state.TurnKey = turnKey;
        _state.Turn++;
        _state.TurnToolCalls = 0;
        Save();
    }

    /// <summary>Updates the ledger after a tool call completed: reads are recorded, edits forget the file.</summary>
    public void Record(HookInput e, DateTimeOffset at)
    {
        _state.TurnToolCalls++;
        if (e.FilePath is not null)
        {
            var probe = new ToolCall { Id = "", Name = e.ToolName };
            if (ToolKinds.IsEdit(probe))
                _state.Reads.RemoveAll(r => r.Path == e.FilePath);
            else if (ToolKinds.IsRead(probe))
            {
                _state.Reads.RemoveAll(r => r.Path == e.FilePath && r.Range == e.Range);
                _state.Reads.Add(new LedgerRead
                {
                    Path = e.FilePath, Range = e.Range, At = at.ToUnixTimeMilliseconds(),
                    Chars = (int)Math.Clamp(e.ResponseChars ?? 1, 1, int.MaxValue),
                });
            }
        }
        Save();
    }

    /// <summary>The ledger as a session the guardrail engine can reason about.</summary>
    public AgentSession AsSession(string sessionKey)
    {
        var s = new AgentSession { SessionId = sessionKey, Source = "hook-ledger" };
        var history = new Turn { Index = 0, Prompt = "" };
        foreach (var r in _state.Reads)
            history.Tools.Add(new ToolCall
            {
                Id = $"{r.Path}#{r.Range}", Name = "Read", Target = r.Path, Range = r.Range, ResultChars = Math.Max(1, r.Chars),
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.At),
            });
        s.Turns.Add(history);

        // The current turn: as many placeholder tool calls as were made, for the tool-call budget.
        var current = new Turn { Index = _state.Turn + 1, Prompt = "" };
        for (var i = 0; i < _state.TurnToolCalls; i++) current.Tools.Add(new ToolCall { Id = $"t{i}", Name = "tool" });
        s.Turns.Add(current);
        return s;
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_state, LedgerJsonContext.Default.LedgerState)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed class LedgerState
{
    public List<LedgerRead> Reads { get; set; } = new();
    public int Turn { get; set; }
    public string? TurnKey { get; set; }
    public int TurnToolCalls { get; set; }
}

internal sealed class LedgerRead
{
    public string Path { get; set; } = "";
    public string? Range { get; set; }
    public long At { get; set; }
    public int Chars { get; set; } = 1;
}

[JsonSerializable(typeof(LedgerState))]
internal sealed partial class LedgerJsonContext : JsonSerializerContext;
