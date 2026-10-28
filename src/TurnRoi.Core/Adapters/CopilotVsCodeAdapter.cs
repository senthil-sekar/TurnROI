using System.Text.Json;
using System.Text.Json.Nodes;
using TurnRoi.Core.Model;

namespace TurnRoi.Core.Adapters;

/// <summary>
/// Parses GitHub Copilot Chat sessions stored by VS Code
/// (&lt;User&gt;/workspaceStorage/&lt;hash&gt;/chatSessions/&lt;id&gt;.jsonl or .json).
///
/// Format notes (internal to VS Code, undocumented, subject to change):
/// <list type="bullet">
/// <item>.jsonl is a mutation log: <c>{"kind":0,"v":state}</c> sets the whole state, then kind 1 sets a value at path
/// <c>k</c>, kind 2 pushes <c>v</c> onto the array at <c>k</c> (optionally truncating to index <c>i</c> first),
/// kind 3 deletes the value at <c>k</c>. Older VS Code versions write a single .json snapshot.</item>
/// <item>Each <c>requests[]</c> entry is one human turn. Token totals per model live in <c>modelTotals[]</c>
/// (<c>inputTokens</c> includes cached tokens, <c>cachedTokens</c>, <c>outputTokens</c>); older records only carry
/// <c>promptTokens</c>/<c>completionTokens</c> for the last model call of the turn.</item>
/// </list>
/// Copilot reports totals per turn, not per model call, so each <see cref="LlmCall"/> here is one model's aggregate
/// for the turn, and per-call tool output sizes are not available.
/// </summary>
public sealed class CopilotVsCodeAdapter : IAgentSourceAdapter
{
    public string Name => "copilot-vscode";

    public bool CanRead(string path)
    {
        if (!File.Exists(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".json" or ".jsonl")) return false;
        if (Path.GetDirectoryName(path) is { } dir &&
            (dir.EndsWith("chatSessions", StringComparison.Ordinal) || dir.EndsWith("emptyWindowChatSessions", StringComparison.Ordinal)))
            return true;

        // Fall back to sniffing the start of the file: a mutation log opens with kind 0; a snapshot has requests[].
        using var reader = new StreamReader(path);
        var buffer = new char[4096];
        var head = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
        return head.TrimStart().StartsWith("{\"kind\":0", StringComparison.Ordinal)
            || (head.Contains("\"requests\"", StringComparison.Ordinal) && !head.Contains("\"parentUuid\"", StringComparison.Ordinal));
    }

    public AgentSession Read(string path)
    {
        var fallbackId = Path.GetFileNameWithoutExtension(path);
        var isLog = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
        var session = isLog
            ? ParseLog(File.ReadLines(path), fallbackId)
            : ParseSnapshot(File.ReadAllText(path), fallbackId);

        session.Cwd ??= TryReadWorkspaceFolder(path);
        return session;
    }

    public AgentSession ParseSnapshot(string json, string fallbackSessionId = "unknown")
    {
        JsonNode? state;
        try { state = JsonNode.Parse(json); }
        catch (JsonException)
        {
            var broken = new AgentSession { SessionId = fallbackSessionId, Source = Name, SkippedLines = 1 };
            return broken;
        }
        return Build(state, fallbackSessionId, skipped: 0);
    }

    public AgentSession ParseLog(IEnumerable<string> lines, string fallbackSessionId = "unknown")
    {
        JsonNode? state = null;
        var skipped = 0;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            JsonObject entry;
            try
            {
                if (JsonNode.Parse(raw) is not JsonObject obj) { skipped++; continue; }
                entry = obj;
            }
            catch (JsonException) { skipped++; continue; }

            var kind = entry["kind"] is JsonValue kv && kv.TryGetValue<int>(out var k) ? k : -1;
            var value = entry["v"]?.DeepClone();
            var key = entry["k"] as JsonArray;

            try
            {
                switch (kind)
                {
                    case 0:
                        state = value;
                        break;
                    case 1:
                        state = SetAt(state, key, value);
                        break;
                    case 2:
                        PushAt(state, key, value, entry["i"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : null);
                        break;
                    case 3:
                        DeleteAt(state, key);
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                skipped++; // A mutation that doesn't fit the state we rebuilt; keep going.
            }
        }

        return Build(state, fallbackSessionId, skipped);
    }

    private AgentSession Build(JsonNode? state, string fallbackSessionId, int skipped)
    {
        var session = new AgentSession
        {
            SessionId = Str(state?["sessionId"]) ?? fallbackSessionId,
            Source = Name,
            Cwd = Str(state?["workingDirectory"]),
            SkippedLines = skipped,
        };

        if (state?["requests"] is not JsonArray requests) return session;

        var withoutTokens = 0;
        var lastCallOnly = 0;
        var index = 0;

        foreach (var node in requests)
        {
            if (node is not JsonObject req) continue;
            index++;

            var prompt = Str(req["message"]?["text"]) ?? "";
            var ts = Long(req["timestamp"]) is { } ms && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
            var requestId = Str(req["requestId"]) ?? $"request-{index}";
            var turnModel = NormalizeModel(Str(req["result"]?["metadata"]?["resolvedModel"]) ?? Str(req["modelId"]));

            var turn = new Turn { Index = index, Prompt = prompt.Trim(), StartedAt = ts };

            var totals = FindArray(req, "modelTotals");
            if (totals is { Count: > 0 })
            {
                var n = 0;
                foreach (var t in totals.OfType<JsonObject>())
                {
                    var model = NormalizeModel(Str(t["model"]) ?? Str(t["modelId"]) ?? Str(t["id"])) ?? turnModel ?? "unknown";
                    var input = Long(t["inputTokens"]) ?? 0;
                    var cached = Long(t["cachedTokens"]) ?? 0;
                    var output = Long(t["outputTokens"]) ?? 0;
                    turn.Calls.Add(new LlmCall($"{requestId}#{n++}", model, ts,
                        new TokenUsage(Math.Max(0, input - cached), output, 0, cached), false));
                }
            }
            else if (FindNumber(req, "promptTokens") is { } promptTokens)
            {
                var output = FindNumber(req, "completionTokens") ?? FindNumber(req, "outputTokens") ?? 0;
                turn.Calls.Add(new LlmCall(requestId, turnModel ?? "unknown", ts, new TokenUsage(promptTokens, output, 0, 0), false));
                lastCallOnly++;
            }
            else
            {
                withoutTokens++;
            }

            if (req["response"] is JsonArray response)
            {
                var toolN = 0;
                foreach (var part in response.OfType<JsonObject>())
                {
                    if (Str(part["kind"]) != "toolInvocationSerialized") continue;
                    turn.Tools.Add(new ToolCall
                    {
                        Id = Str(part["toolCallId"]) ?? $"{requestId}-tool-{toolN}",
                        Name = Str(part["toolId"]) ?? Str(part["toolName"]) ?? "tool",
                        Target = Str(part["toolSpecificData"]?["commandLine"]?["original"])
                                 ?? Str(part["toolSpecificData"]?["command"]),
                    });
                    toolN++;
                }
            }

            session.Turns.Add(turn);
        }

        if (withoutTokens > 0)
            session.Notes.Add($"{withoutTokens} Copilot turn(s) had no token data (cancelled, failed, or an older VS Code version).");
        if (lastCallOnly > 0)
            session.Notes.Add($"{lastCallOnly} Copilot turn(s) only record the last model call's tokens, so they undercount.");
        session.Notes.Add("Copilot bills premium requests, not tokens: cost shown is the API-equivalent price for comparison.");

        return session;
    }

    // ---- mutation log helpers ----

    private static JsonNode? SetAt(JsonNode? root, JsonArray? path, JsonNode? value)
    {
        if (path is null || path.Count == 0) return value;
        var (parent, last) = Navigate(root, path);
        switch (parent)
        {
            case JsonObject o:
                o[KeyString(last)] = value;
                break;
            case JsonArray a:
                var idx = KeyIndex(last);
                while (a.Count <= idx) a.Add(null);
                a[idx] = value;
                break;
            default:
                throw new InvalidOperationException("Set target is not a container.");
        }
        return root;
    }

    private static void PushAt(JsonNode? root, JsonArray? path, JsonNode? value, int? truncateTo)
    {
        var target = path is null || path.Count == 0 ? root : Get(root, path);
        if (target is null && path is { Count: > 0 })
        {
            SetAt(root, path, new JsonArray());
            target = Get(root, path);
        }
        if (target is not JsonArray arr) throw new InvalidOperationException("Push target is not an array.");

        if (truncateTo is { } t)
        {
            while (arr.Count > t) arr.RemoveAt(arr.Count - 1);
            while (arr.Count < t) arr.Add(null);
        }

        if (value is JsonArray items)
        {
            foreach (var item in items.ToList())
            {
                items.Remove(item);
                arr.Add(item);
            }
        }
        else if (value is not null)
        {
            arr.Add(value);
        }
    }

    private static void DeleteAt(JsonNode? root, JsonArray? path)
    {
        if (path is null || path.Count == 0) return;
        var (parent, last) = Navigate(root, path);
        switch (parent)
        {
            case JsonObject o: o.Remove(KeyString(last)); break;
            case JsonArray a when KeyIndex(last) < a.Count: a.RemoveAt(KeyIndex(last)); break;
        }
    }

    private static (JsonNode? Parent, JsonNode? Last) Navigate(JsonNode? root, JsonArray path)
    {
        var parent = root;
        for (var i = 0; i < path.Count - 1; i++)
            parent = Step(parent, path[i]) ?? throw new InvalidOperationException("Path not found.");
        return (parent, path[^1]);
    }

    private static JsonNode? Get(JsonNode? root, JsonArray path)
    {
        var node = root;
        foreach (var segment in path)
        {
            node = Step(node, segment);
            if (node is null) return null;
        }
        return node;
    }

    private static JsonNode? Step(JsonNode? node, JsonNode? segment) => node switch
    {
        JsonObject o => o[KeyString(segment)],
        JsonArray a => KeyIndex(segment) is var i && i >= 0 && i < a.Count ? a[i] : null,
        _ => null,
    };

    private static string KeyString(JsonNode? segment) =>
        segment is JsonValue v && v.TryGetValue<string>(out var s) ? s : segment?.ToJsonString() ?? "";

    private static int KeyIndex(JsonNode? segment) =>
        segment is JsonValue v && v.TryGetValue<int>(out var i) ? i
        : segment is JsonValue sv && sv.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p
        : throw new ArgumentException("Array index expected.");

    // ---- field helpers ----

    /// <summary>Looks for a field on the request, then under result, result.metadata and usage.</summary>
    private static JsonArray? FindArray(JsonObject req, string name) =>
        req[name] as JsonArray ?? req["result"]?[name] as JsonArray ?? req["result"]?["metadata"]?[name] as JsonArray;

    private static long? FindNumber(JsonObject req, string name) =>
        Long(req[name]) ?? Long(req["result"]?["metadata"]?[name]) ?? Long(req["result"]?["usage"]?[name]) ?? Long(req["usage"]?[name]);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonNode? n) =>
        n is JsonValue v && (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : (long?)null) is { } r ? r : null;

    /// <summary>"copilot/claude-sonnet-4.5" → "claude-sonnet-4.5".</summary>
    internal static string? NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var slash = model.LastIndexOf('/');
        return slash >= 0 ? model[(slash + 1)..] : model;
    }

    private static string? TryReadWorkspaceFolder(string sessionPath)
    {
        try
        {
            var chatDir = Path.GetDirectoryName(sessionPath);
            var wsDir = chatDir is null ? null : Path.GetDirectoryName(chatDir);
            var file = wsDir is null ? null : Path.Combine(wsDir, "workspace.json");
            if (file is null || !File.Exists(file)) return null;
            var folder = Str(JsonNode.Parse(File.ReadAllText(file))?["folder"]);
            return folder is not null && Uri.TryCreate(folder, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : folder;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
