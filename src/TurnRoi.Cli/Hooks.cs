using System.Text.Json.Nodes;
using TurnRoi.Core.Adapters;
using TurnRoi.Core.Apply;
using TurnRoi.Core.Guardrails;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Cli;

/// <summary>
/// Hook entry point (<c>turnroi hook pre-tool-use|post-tool-use [--agent claude|cursor|copilot]</c>) and installer
/// (<c>turnroi hooks install|uninstall|print</c>). Any internal failure lets the agent carry on.
/// </summary>
internal static class Hooks
{
    private static string StateDir => Path.Combine(Path.GetTempPath(), "turnroi");

    public static int Run(string? eventName, TargetAgent? agentHint, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var phase = eventName == "post-tool-use" ? HookPhase.PostToolUse : HookPhase.PreToolUse;
        HookOutput output;
        try
        {
            if (eventName is not ("pre-tool-use" or "post-tool-use") || JsonNode.Parse(stdin.ReadToEnd()) is not JsonObject payload)
            {
                output = HookProtocol.FailOpen(agentHint, phase);
            }
            else
            {
                var e = HookProtocol.Parse(payload, agentHint);
                output = HookProtocol.Format(e.Agent, phase, Decide(e, phase));
            }
        }
        catch (Exception)
        {
            output = HookProtocol.FailOpen(agentHint, phase);
        }

        if (output.Stdout is not null) stdout.WriteLine(output.Stdout);
        if (output.Stderr is not null) stderr.WriteLine(output.Stderr);
        return output.ExitCode;
    }

    private static HookDecision Decide(HookInput e, HookPhase phase)
    {
        var config = GuardrailConfig.Load(e.ProjectDir);
        var engine = new GuardrailEngine(config, PriceTable.LoadDefault(), Modified);
        var hookEvent = new HookEvent(e.ToolName, e.FilePath, e.Range, e.ResponseChars);

        // Claude Code transcripts give the full picture (including token use); other agents use TurnROI's read ledger.
        // (VS Code Copilot also runs hooks from .claude/settings.json, with its own transcript format: use the ledger there.)
        var useTranscript = e.Agent == TargetAgent.Claude && e.TranscriptPath is { } t && File.Exists(t) &&
                            (t.Contains("/.claude/", StringComparison.Ordinal) || t.Contains("\\.claude\\", StringComparison.Ordinal) ||
                             Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } cfg && t.StartsWith(cfg, StringComparison.Ordinal));
        var ledger = useTranscript ? null : ReadLedger.Open(StateDir, e.Agent, e.SessionKey);
        var session = useTranscript ? new ClaudeCodeJsonlAdapter().Read(e.TranscriptPath!) : ledger!.AsSession(e.SessionKey);

        if (phase == HookPhase.PreToolUse) return engine.PreToolUse(hookEvent, session);

        var decision = engine.PostToolUse(hookEvent, session, FirstTime);
        ledger?.Record(e, DateTimeOffset.UtcNow);
        return decision;
    }

    public static int Manage(CliArgs opts)
    {
        var action = opts.Target ?? "print";
        var agents = opts.Agents.Count > 0 ? opts.Agents : [TargetAgent.Claude];
        var command = opts.HookCommand ?? "turnroi";
        var projectDir = opts.UserScope
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(opts.ProjectDir ?? Directory.GetCurrentDirectory());

        if (opts.UserScope && agents.Contains(TargetAgent.Copilot))
        {
            Console.Error.WriteLine("error: Copilot hooks are per repository (.github/hooks); drop --user for copilot.");
            return 2;
        }

        switch (action)
        {
            case "print":
                foreach (var agent in agents)
                {
                    var (changes, _) = ApplyPlanner.Plan(projectDir, null,
                        new ApplyOptions([agent], Rules: false, Skill: false, Config: false, HookCommand: command, SharedClaudeSettings: opts.Shared || opts.UserScope),
                        _ => null);
                    foreach (var c in changes)
                    {
                        Console.WriteLine($"# {agent}: {Path.GetRelativePath(projectDir, c.Path)}");
                        Console.WriteLine(c.After);
                    }
                }
                return 0;

            case "install":
            {
                var (changes, skipped) = ApplyPlanner.Plan(projectDir, null,
                    new ApplyOptions(agents, Rules: false, Skill: false, Config: false, HookCommand: command, SharedClaudeSettings: opts.Shared || opts.UserScope));
                foreach (var s in skipped) Console.Error.WriteLine(Ansi.Yellow($"skipped {s}"));

                var configPath = Path.Combine(projectDir, GuardrailConfig.FileName);
                var extra = !opts.UserScope && !File.Exists(configPath)
                    ? new[] { new FileChange(configPath, null, new GuardrailConfig().ToJson() + "\n", "guardrail settings") }
                    : [];
                var all = changes.Concat(extra).ToList();
                if (all.Count == 0) { Console.WriteLine("TurnROI hooks are already installed."); return 0; }

                ApplyRunner.Apply(projectDir, all);
                foreach (var c in all) Console.WriteLine($"{(c.IsCreate ? "created" : "updated")} {Path.GetRelativePath(projectDir, c.Path)}  ({c.Description})");
                Console.WriteLine("Restart the agent (or reload its hooks) for them to take effect. Undo with 'turnroi profile --undo'.");
                if (opts.HookCommand is null)
                    Console.WriteLine("The hooks call 'turnroi'; make sure it is on PATH (dotnet tool install -g TurnROI) or pass --command.");
                return 0;
            }

            case "uninstall":
            {
                var changes = ApplyPlanner.PlanHookRemoval(projectDir, agents);
                if (changes.Count == 0) { Console.WriteLine("No TurnROI hooks found."); return 0; }
                ApplyRunner.Apply(projectDir, changes);
                foreach (var c in changes) Console.WriteLine($"{c.Description}: {Path.GetRelativePath(projectDir, c.Path)}");
                return 0;
            }

            default:
                Console.Error.WriteLine($"error: unknown hooks action '{action}' (expected install, uninstall or print)");
                return 2;
        }
    }

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
            Directory.CreateDirectory(StateDir);
            var safe = string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
            var marker = Path.Combine(StateDir, safe);
            if (File.Exists(marker)) return false;
            File.WriteAllText(marker, "");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
