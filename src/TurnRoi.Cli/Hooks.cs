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
        var phase = eventName switch
        {
            "post-tool-use" => HookPhase.PostToolUse,
            "user-prompt" => HookPhase.UserPrompt,
            _ => HookPhase.PreToolUse,
        };
        HookOutput output;
        try
        {
            if (eventName is not ("pre-tool-use" or "post-tool-use" or "user-prompt") || JsonNode.Parse(stdin.ReadToEnd()) is not JsonObject payload)
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
        // Copilot runs hooks from both .github/hooks and .claude/settings.json: answer each tool call once.
        if (e.ToolCallId is { } id && !FirstTime($"seen-{phase}-{e.SessionKey}-{id}")) return HookDecision.Allow;

        var config = GuardrailConfig.Load(e.ProjectDir);
        var prices = PriceTable.LoadDefault();
        var engine = new GuardrailEngine(config, prices, Modified);
        var hookEvent = new HookEvent(e.ToolName, e.FilePath, e.Range, e.ResponseChars);

        var (session, ledger, model) = LoadSession(e);
        if (phase == HookPhase.UserPrompt)
        {
            ledger?.NewTurn();
            return HookDecision.Allow;
        }

        var decision = phase == HookPhase.PreToolUse
            ? engine.PreToolUse(hookEvent, session)
            : engine.PostToolUse(hookEvent, session, FirstTime);
        if (phase == HookPhase.PostToolUse) ledger?.Record(e, DateTimeOffset.UtcNow);

        if (config.LogInterventions && decision.Interventions.Count > 0)
        {
            InterventionLog.Append(decision.Interventions.Select(i => new InterventionRecord
            {
                At = DateTimeOffset.UtcNow,
                Agent = e.Agent.ToString().ToLowerInvariant(),
                Session = e.SessionKey,
                Project = e.ProjectDir,
                Rule = i.Rule,
                Action = i.Action,
                Target = i.Target,
                TokensAvoided = i.TokensAvoided,
                Model = model,
                Detail = i.Detail,
            }));
        }
        return decision;
    }

    /// <summary>
    /// The session as far as the hook can know it. Claude Code: its transcript (tokens, tools, reads). Other agents:
    /// TurnROI's ledger (reads, tool calls this turn), plus token counts from Copilot's chat session file when found.
    /// </summary>
    private static (TurnRoi.Core.Model.AgentSession Session, ReadLedger? Ledger, string? Model) LoadSession(HookInput e)
    {
        var t = e.TranscriptPath;
        var isClaudeTranscript = e.Agent == TargetAgent.Claude && t is not null && File.Exists(t) &&
            (t.Contains("/.claude/", StringComparison.Ordinal) || t.Contains("\\.claude\\", StringComparison.Ordinal) ||
             Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } cfg && t.StartsWith(cfg, StringComparison.Ordinal));
        if (isClaudeTranscript)
        {
            var transcript = new ClaudeCodeJsonlAdapter().Read(t!);
            var lastModel = transcript.Turns.SelectMany(x => x.Calls).LastOrDefault()?.Model;
            return (transcript, null, lastModel);
        }

        var ledger = ReadLedger.Open(StateDir, e.Agent, e.SessionKey);
        ledger.ObserveTurnKey(e.TurnKey);
        var session = ledger.AsSession(e.SessionKey);
        var model = e.Model;

        // Copilot (VS Code) records tokens in its chat session file: use the latest turn's counts for the token budget.
        if (CopilotSessionFile(e) is { } chat)
        {
            try
            {
                var copilot = new CopilotVsCodeAdapter().Read(chat);
                if (copilot.Turns.LastOrDefault() is { } lastTurn)
                {
                    session.Turns[^1].Calls.AddRange(lastTurn.Calls);
                    model ??= lastTurn.Calls.LastOrDefault()?.Model;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        return (session, ledger, model);
    }

    private static string? CopilotSessionFile(HookInput e)
    {
        if (e.Agent is TargetAgent.Cursor) return null;
        if (e.TranscriptPath is { } t && File.Exists(t) && new CopilotVsCodeAdapter().CanRead(t)) return t;
        if (e.Agent is not TargetAgent.Copilot || e.SessionKey == "unknown") return null;
        // VS Code names chat session files by session id.
        return SessionSources.FindSessions(AgentSource.Copilot, null, 50)
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Name).Equals(e.SessionKey, StringComparison.OrdinalIgnoreCase))?.FullName;
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
