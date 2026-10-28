using System.Text.Json;
using System.Text.Json.Nodes;
using TurnRoi.Core.Adapters;
using TurnRoi.Core.Guardrails;
using TurnRoi.Core.Model;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Cli;

/// <summary>
/// Claude Code hook entry points (<c>turnroi hook pre-tool-use|post-tool-use</c>) and installer
/// (<c>turnroi hooks install|uninstall|print</c>).
///
/// Protocol: Claude Code passes the event as JSON on stdin. Exit 0 = carry on. Exit 2 = the message on stderr goes to
/// the agent; for PreToolUse the tool call is also blocked. Any internal failure exits 0, so a TurnROI bug can never
/// stop someone's agent.
/// </summary>
internal static class Hooks
{
    /// <summary>Our entries: any hook command that runs TurnROI's hook subcommand, whatever the install command was.</summary>
    private static bool IsOurs(JsonNode? entry) =>
        entry?.ToJsonString() is { } json &&
        json.Contains("turnroi", StringComparison.OrdinalIgnoreCase) &&
        (json.Contains(" hook pre-tool-use", StringComparison.Ordinal) || json.Contains(" hook post-tool-use", StringComparison.Ordinal));

    public static int Run(string? eventName, TextReader stdin, TextWriter stderr)
    {
        try
        {
            var root = JsonNode.Parse(stdin.ReadToEnd()) as JsonObject;
            if (root is null) return 0;

            var toolName = Str(root["tool_name"]) ?? "";
            var input = root["tool_input"] as JsonObject;
            var filePath = Str(input?["file_path"]) ?? Str(input?["notebook_path"]);
            var range = RangeOf(input);
            var transcript = Str(root["transcript_path"]);
            var projectDir = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") is { Length: > 0 } p ? p : Str(root["cwd"]);
            long? responseChars = root["tool_response"] is { } resp ? ResponseChars(resp) : null;

            var config = GuardrailConfig.Load(projectDir);
            var session = transcript is not null && File.Exists(transcript)
                ? new ClaudeCodeJsonlAdapter().Read(transcript)
                : new AgentSession { SessionId = Str(root["session_id"]) ?? "unknown", Source = "claude-code" };

            var engine = new GuardrailEngine(config, PriceTable.LoadDefault(), Modified);
            var e = new HookEvent(toolName, filePath, range, responseChars);

            var decision = eventName switch
            {
                "pre-tool-use" => engine.PreToolUse(e, session),
                "post-tool-use" => engine.PostToolUse(e, session, FirstTime),
                _ => HookDecision.Allow,
            };

            if (decision.Feedback is null) return 0;
            stderr.WriteLine(decision.Feedback);
            return 2;
        }
        catch (Exception)
        {
            return 0; // Fail open.
        }
    }

    public static int Manage(CliArgs opts)
    {
        var action = opts.Target ?? "print";
        var command = opts.HookCommand ?? "turnroi";
        var settingsPath = SettingsPath(opts);

        switch (action)
        {
            case "print":
                Console.WriteLine(Snippet(command).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return 0;

            case "install":
            {
                var settings = LoadSettings(settingsPath);
                var hooks = settings["hooks"] as JsonObject ?? new JsonObject();
                settings["hooks"] = hooks;
                var replaced = 0;
                foreach (var (evt, entry) in Entries(command))
                {
                    var list = hooks[evt] as JsonArray ?? new JsonArray();
                    hooks[evt] = list;
                    // Re-installing replaces our previous entry (e.g. a different --command) instead of duplicating it.
                    foreach (var old in list.Where(IsOurs).ToList()) { list.Remove(old); replaced++; }
                    list.Add(entry);
                }
                Save(settingsPath, settings);
                Console.WriteLine(replaced > 0
                    ? $"Updated TurnROI hooks in {settingsPath}"
                    : $"Added TurnROI hooks to {settingsPath}");

                if (!opts.UserScope)
                {
                    var configPath = Path.Combine(ProjectDir(opts), GuardrailConfig.FileName);
                    if (!File.Exists(configPath))
                    {
                        File.WriteAllText(configPath, new GuardrailConfig().ToJson() + Environment.NewLine);
                        Console.WriteLine($"Wrote default guardrail settings to {configPath}");
                    }
                }
                Console.WriteLine("Restart Claude Code (or open /hooks) for the hooks to take effect.");
                if (opts.HookCommand is null)
                    Console.WriteLine("The hooks call 'turnroi'; make sure it is on PATH (dotnet tool install -g TurnROI) or pass --command.");
                return 0;
            }

            case "uninstall":
            {
                if (!File.Exists(settingsPath)) { Console.WriteLine($"Nothing to remove: {settingsPath} does not exist."); return 0; }
                var settings = LoadSettings(settingsPath);
                var removed = 0;
                if (settings["hooks"] is JsonObject hooks)
                {
                    foreach (var evt in hooks.Select(kv => kv.Key).ToList())
                    {
                        if (hooks[evt] is not JsonArray list) continue;
                        foreach (var item in list.ToList())
                        {
                            if (!IsOurs(item)) continue;
                            list.Remove(item);
                            removed++;
                        }
                        if (list.Count == 0) hooks.Remove(evt);
                    }
                    if (hooks.Count == 0) settings.Remove("hooks");
                }
                Save(settingsPath, settings);
                Console.WriteLine(removed == 0 ? "No TurnROI hooks found." : $"Removed {removed} TurnROI hook(s) from {settingsPath}");
                return 0;
            }

            default:
                Console.Error.WriteLine($"error: unknown hooks action '{action}' (expected install, uninstall or print)");
                return 2;
        }
    }

    private static IEnumerable<(string Event, JsonObject Entry)> Entries(string command) =>
    [
        ("PreToolUse", Entry($"{command} hook pre-tool-use")),
        ("PostToolUse", Entry($"{command} hook post-tool-use")),
    ];

    private static JsonObject Entry(string cmd) => new()
    {
        ["matcher"] = "*",
        ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = cmd, ["timeout"] = 10 }),
    };

    private static JsonObject Snippet(string command)
    {
        var hooks = new JsonObject();
        foreach (var (evt, entry) in Entries(command)) hooks[evt] = new JsonArray(entry);
        return new JsonObject { ["hooks"] = hooks };
    }

    private static string ProjectDir(CliArgs opts) => Path.GetFullPath(opts.Root ?? Directory.GetCurrentDirectory());

    private static string SettingsPath(CliArgs opts)
    {
        if (opts.UserScope)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
        return Path.Combine(ProjectDir(opts), ".claude", opts.Shared ? "settings.json" : "settings.local.json");
    }

    private static JsonObject LoadSettings(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidOperationException($"{path} is not a JSON object");
    }

    private static void Save(string path, JsonObject settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static string? RangeOf(JsonObject? input)
    {
        if (input is null) return null;
        var parts = new List<string>();
        foreach (var key in new[] { "offset", "limit" })
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

    private static DateTimeOffset? Modified(string path)
    {
        try { return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>True the first time a key is seen (marker file in temp), so one-off warnings aren't repeated.</summary>
    private static bool FirstTime(string key)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "turnroi");
            Directory.CreateDirectory(dir);
            var safe = string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
            var marker = Path.Combine(dir, safe);
            if (File.Exists(marker)) return false;
            File.WriteAllText(marker, "");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
