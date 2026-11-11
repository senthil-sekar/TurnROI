using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TurnRoi.Cli;

/// <summary>
/// <c>turnroi mcp</c>: a Model Context Protocol server on stdio (newline-delimited JSON-RPC 2.0), so an agent can ask what
/// its own session cost, which turns were wasted, and what the repo's waste patterns are, without leaving the chat.
/// Each tool runs the matching read-only CLI command and returns its text (or JSON) output. Nothing here writes files.
/// </summary>
internal static class McpServer
{
    private const string LatestProtocol = "2025-06-18";

    private sealed record Tool(string Name, string Description, JsonObject Schema, Func<JsonObject, List<string>> Args);

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonObject Schema(params (string Name, JsonObject Prop)[] props)
    {
        var p = new JsonObject();
        foreach (var (name, prop) in props) p[name] = prop.DeepClone();
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["additionalProperties"] = false };
    }

    private static readonly (string, JsonObject) Source = ("source",
        Prop("string", "Agent whose sessions to read: claude (default), copilot, copilot-cli, codex, cursor, opencode, otel."));
    private static readonly (string, JsonObject) Root = ("root", Prop("string", "Override where sessions are searched for (or the export file)."));

    private static readonly Tool[] Tools =
    [
        new("list_sessions",
            "List recent coding-agent sessions on this machine (newest first) with their folder and title.",
            Schema(Source, Root, ("limit", Prop("integer", "How many (default 20)."))),
            a => ["sessions", .. Common(a), .. Opt(a, "limit", "--limit")]),

        new("analyze_session",
            "Per-turn token spend, cost and outcome (productive, unverified, failing, wasted, exploratory) for one session, " +
            "plus waste findings: unchanged files re-read, oversized tool output, low cache hit, turns the user pushed back on. " +
            "Use session=\"latest\" for the session you are in.",
            Schema(("session", Prop("string", "\"latest\" (default), a session id or prefix, or a transcript file path.")),
                Source, Root,
                ("top", Prop("integer", "Show the n most expensive turns (default 15).")),
                ("allTurns", Prop("boolean", "Show every turn in order instead of the most expensive.")),
                ("json", Prop("boolean", "Return the machine-readable report (schema v2) instead of text."))),
            a =>
            {
                var args = new List<string> { "analyze", Str(a, "session") ?? "latest" };
                args.AddRange(Common(a));
                args.AddRange(Opt(a, "top", "--top"));
                if (Bool(a, "allTurns")) args.Add("--all");
                if (Bool(a, "json")) args.Add("--json");
                return args;
            }),

        new("waste_profile",
            "Waste patterns across many sessions (re-reads, noisy commands, pushback, cache misses) and the fixes TurnROI " +
            "would propose for this repo. Read-only: it never writes the fixes.",
            Schema(Source, Root,
                ("project", Prop("string", "Only sessions whose folder contains this text.")),
                ("since", Prop("string", "Only sessions started on or after this date (e.g. 2026-11-01).")),
                ("limit", Prop("integer", "How many recent sessions to read (default 100)."))),
            a => ["profile", .. Common(a), .. Opt(a, "project", "--project"), .. Opt(a, "since", "--since"), .. Opt(a, "limit", "--limit")]),

        new("guardrail_savings",
            "What TurnROI's hooks blocked or nudged (re-reads, oversized output, turn budgets) and the tokens that avoided.",
            Schema(("since", Prop("string", "Only interventions on or after this date.")),
                ("project", Prop("string", "Only interventions in folders containing this text.")),
                ("json", Prop("boolean", "Return JSON instead of text."))),
            a =>
            {
                var args = new List<string> { "savings" };
                args.AddRange(Opt(a, "since", "--since"));
                args.AddRange(Opt(a, "project", "--project"));
                if (Bool(a, "json")) args.Add("--json");
                return args;
            }),

        new("check_skill_freshness",
            "Whether the file outlines (line numbers) in the TurnROI repo skill still match the files. Stale outlines point " +
            "at the wrong lines: prefer searching the file, and ask the user to run 'turnroi profile --refresh'.",
            Schema(("dir", Prop("string", "Project folder (default: the server's working folder)."))),
            a => ["profile", "--check", .. Opt(a, "dir", "--dir")]),
    ];

    public static int Run(TextReader input, TextWriter output, Func<string[], int> runCli)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? reply;
            try { reply = Handle(line, runCli); }
            catch (Exception ex) { reply = Error(null, -32603, $"internal error: {ex.Message}"); }
            if (reply is null) continue;
            output.WriteLine(reply.ToJsonString());
            output.Flush();
        }
        return 0;
    }

    /// <summary>Handles one JSON-RPC message; returns the response, or null for notifications.</summary>
    public static JsonObject? Handle(string line, Func<string[], int> runCli)
    {
        JsonObject msg;
        try
        {
            msg = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return Error(null, -32700, "parse error");
        }

        var id = msg["id"]?.DeepClone();
        var method = msg["method"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
        if (id is null) return null; // notification (initialized, cancelled, ...) or a response to us
        if (method is null) return Error(id, -32600, "invalid request");

        var p = msg["params"] as JsonObject ?? new JsonObject();
        switch (method)
        {
            case "initialize":
                var requested = Str(p, "protocolVersion");
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = requested ?? LatestProtocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "turnroi",
                        ["version"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev",
                    },
                    ["instructions"] =
                        "TurnROI measures what each agent turn cost and whether it paid off. Call analyze_session with " +
                        "session=\"latest\" to review this session's spend before a long task or when a turn feels expensive.",
                });

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                var list = new JsonArray();
                foreach (var t in Tools)
                    list.Add((JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.Schema.DeepClone(),
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
                    });
                return Result(id, new JsonObject { ["tools"] = list });

            case "tools/call":
                var name = Str(p, "name");
                var tool = Tools.FirstOrDefault(t => t.Name == name);
                if (tool is null) return Error(id, -32602, $"unknown tool '{name}'");
                var arguments = p["arguments"] as JsonObject ?? new JsonObject();
                var (code, text) = Invoke(tool.Args(arguments), runCli);
                return Result(id, new JsonObject
                {
                    ["content"] = new JsonArray((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = code != 0,
                });

            default:
                return Error(id, -32601, $"method not found: {method}");
        }
    }

    private static (int Code, string Text) Invoke(List<string> args, Func<string[], int> runCli)
    {
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        var (sout, serr) = (new StringWriter(), new StringWriter());
        int code;
        try
        {
            Console.SetOut(sout);
            Console.SetError(serr);
            code = runCli(args.ToArray());
        }
        catch (Exception ex)
        {
            code = 1;
            serr.WriteLine($"error: {ex.Message}");
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
        var text = sout.ToString().TrimEnd();
        var err = serr.ToString().TrimEnd();
        if (err.Length > 0) text = text.Length == 0 ? err : $"{text}\n\n{err}";
        return (code, text.Length == 0 ? "(no output)" : text);
    }

    private static IEnumerable<string> Common(JsonObject a) => [.. Opt(a, "source", "--source"), .. Opt(a, "root", "--root")];

    private static IEnumerable<string> Opt(JsonObject a, string key, string flag) =>
        a[key] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => [flag, s],
            JsonValue v when v.TryGetValue<long>(out var n) => [flag, n.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            JsonValue v when v.TryGetValue<double>(out var d) => [flag, ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            _ => [],
        };

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
