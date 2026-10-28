using TurnRoi.Core.Model;

namespace TurnRoi.Core.Analysis;

/// <summary>A suggested change, with the evidence behind it. TurnROI proposes; the user decides and applies.</summary>
public sealed record Proposal(string Title, string Evidence, string Action);

public sealed record RepeatedFile(string File, int Rereads, int Sessions, long Tokens);

public sealed record NoisyCommand(string Command, int Runs, long Tokens);

/// <summary>
/// The "slow loop": waste patterns aggregated over many sessions of one repo and agent, plus proposed fixes.
/// </summary>
public sealed record WasteProfile
{
    public required int Sessions { get; init; }
    public required int Turns { get; init; }
    public required decimal? Cost { get; init; }
    public required long Tokens { get; init; }
    public required double? CacheHitRatio { get; init; }
    public required IReadOnlyList<OutcomeTotal> Outcomes { get; init; }
    public required IReadOnlyList<RepeatedFile> RepeatedFiles { get; init; }
    public required IReadOnlyList<NoisyCommand> NoisyCommands { get; init; }
    public required int LowCacheTurns { get; init; }
    public required long LargestTurnTokens { get; init; }
    public required string? CommonTestCommand { get; init; }
    public required IReadOnlyList<Proposal> Proposals { get; init; }

    /// <summary>Share of spend (cost when fully priced, else tokens) that went to an outcome.</summary>
    public double Share(TurnOutcome outcome) => ShareOf(Outcomes, outcome);

    public decimal? CostPerTurn => Turns == 0 || Cost is null ? null : Cost / Turns;
    public long TokensPerTurn => Turns == 0 ? 0 : Tokens / Turns;

    internal static double ShareOf(IReadOnlyList<OutcomeTotal> outcomes, TurnOutcome outcome)
    {
        var priced = outcomes.All(o => o.Cost is not null) && outcomes.Sum(o => o.Cost ?? 0) > 0;
        double total = priced ? (double)outcomes.Sum(o => o.Cost ?? 0) : outcomes.Sum(o => o.Tokens);
        if (total <= 0) return 0;
        var part = outcomes.Where(o => o.Outcome == outcome).Sum(o => priced ? (double)(o.Cost ?? 0) : o.Tokens);
        return part / total;
    }

    public static WasteProfile Build(IReadOnlyList<SessionReport> reports)
    {
        var turns = reports.SelectMany(r => r.Turns).Where(t => t.Turn.Calls.Count > 0).ToList();
        var calls = turns.SelectMany(t => t.Turn.Calls).ToList();
        var usage = calls.Aggregate(TokenUsage.Zero, (acc, c) => acc + c.Usage);
        decimal? cost = turns.Any(t => t.Cost is null) ? null : turns.Sum(t => t.Cost ?? 0);

        var repeated = reports
            .SelectMany(r => r.Findings.Where(f => f.Kind == FindingKind.RepeatedRead && f.Subject is not null)
                .Select(f => (r.Session.SessionId, f.Subject!, f.EstimatedTokens ?? 0)))
            .GroupBy(x => x.Item2)
            .Select(g => new RepeatedFile(g.Key, g.Count(), g.Select(x => x.SessionId).Distinct().Count(), g.Sum(x => x.Item3)))
            .OrderByDescending(f => f.Tokens).ThenByDescending(f => f.Rereads)
            .Take(8).ToList();

        var shellTools = turns.SelectMany(t => t.Turn.Tools).Where(t => ToolKinds.IsShell(t) && t.Target is not null).ToList();
        var noisy = shellTools
            .Where(t => t.EstimatedResultTokens is > 0)
            .GroupBy(t => ToolKinds.CommandLabel(t.Target!))
            .Select(g => new NoisyCommand(g.Key, g.Count(), g.Sum(t => t.EstimatedResultTokens ?? 0)))
            .Where(c => c.Tokens >= 5_000)
            .OrderByDescending(c => c.Tokens)
            .Take(8).ToList();

        var testCommand = shellTools.Where(ToolKinds.IsTestRun)
            .GroupBy(t => ToolKinds.CommandLabel(t.Target!))
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key).FirstOrDefault();

        var lowCache = reports.SelectMany(r => r.Findings).Count(f => f.Kind == FindingKind.LowCacheHit);
        var outcomes = OutcomeClassifier.Summarize(reports.SelectMany(r => r.Turns));

        var profile = new WasteProfile
        {
            Sessions = reports.Count,
            Turns = turns.Count,
            Cost = cost,
            Tokens = usage.Total,
            CacheHitRatio = usage.CacheHitRatio,
            Outcomes = outcomes,
            RepeatedFiles = repeated,
            NoisyCommands = noisy,
            LowCacheTurns = lowCache,
            LargestTurnTokens = turns.Count == 0 ? 0 : turns.Max(t => t.Turn.Usage.Total),
            CommonTestCommand = testCommand,
            Proposals = [],
        };

        return profile with { Proposals = Propose(profile) };
    }

    private static List<Proposal> Propose(WasteProfile p)
    {
        var proposals = new List<Proposal>();
        if (p.Turns == 0) return proposals;

        foreach (var cmd in p.NoisyCommands.Where(c => c.Tokens >= 20_000).Take(3))
        {
            proposals.Add(new Proposal(
                $"Trim the output of `{cmd.Command}`",
                $"{cmd.Runs} run(s) put ~{cmd.Tokens:N0} tokens of output into context.",
                $"Add to CLAUDE.md: \"Run `{cmd.Command}` with quiet/summary output (or pipe through `tail -n 40`); " +
                "only show full logs for failures.\" Keep the output nudge on (`outputWarnTokens` in .turnroi.json)."));
        }

        var rereadTokens = p.RepeatedFiles.Sum(f => f.Tokens);
        var rereads = p.RepeatedFiles.Sum(f => f.Rereads);
        if (rereads >= 5 || rereadTokens >= 10_000)
        {
            proposals.Add(new Proposal(
                "Block re-reads of unchanged files",
                $"{rereads} re-read(s) of files that had not changed, ~{rereadTokens:N0} tokens.",
                "Run `turnroi hooks install` (the re-read guard is on by default: `rereadGuard: \"block\"`)."));
        }

        foreach (var f in p.RepeatedFiles.Where(f => f.Sessions >= 3).Take(2))
        {
            proposals.Add(new Proposal(
                $"Summarize {Path.GetFileName(f.File)} for the agent",
                $"Re-read without changes in {f.Sessions} sessions.",
                $"Add the key types, contracts and invariants of `{f.File}` to CLAUDE.md so the agent can skip reading it in full."));
        }

        var wasted = p.Share(TurnOutcome.Wasted);
        if (wasted >= 0.15)
        {
            proposals.Add(new Proposal(
                "Agree on the plan before the agent writes code",
                $"{wasted * 100:0}% of spend went to turns you pushed back on.",
                "For multi-file or ambiguous changes, ask for a plan first (plan mode) and state acceptance criteria " +
                "and constraints in the first prompt."));
        }

        var failing = p.Share(TurnOutcome.Failing);
        var unverified = p.Share(TurnOutcome.Unverified);
        if (failing >= 0.10 || unverified >= 0.30)
        {
            var test = p.CommonTestCommand ?? "the test suite";
            var evidence = new List<string>();
            if (failing >= 0.10) evidence.Add($"{failing * 100:0}% of spend ended with tests failing");
            if (unverified >= 0.30) evidence.Add($"{unverified * 100:0}% changed code with no test run or commit");
            proposals.Add(new Proposal(
                "Make the agent verify before it stops",
                string.Join("; ", evidence) + ".",
                $"Add to CLAUDE.md: \"Before finishing a turn that changes code, run `{test}` and fix failures.\""));
        }

        if (p.Turns >= 5 && p.LowCacheTurns >= Math.Max(2, p.Turns / 5))
        {
            proposals.Add(new Proposal(
                "Keep the prompt prefix stable so caching works",
                $"{p.LowCacheTurns} large turn(s) got less than half of their context from cache.",
                "Avoid editing CLAUDE.md, MCP servers or tool settings mid-session, and continue sessions rather than " +
                "restarting them for the same task."));
        }

        if (p.LargestTurnTokens >= 5_000_000)
        {
            proposals.Add(new Proposal(
                "Set a turn budget",
                $"The largest turn used {p.LargestTurnTokens:N0} tokens.",
                "Set `turnBudgetTokens` or `turnBudgetUsd` in .turnroi.json (default action warns the agent to wrap up; " +
                "`budgetAction: \"block\"` stops tool calls)."));
        }

        return proposals;
    }

    /// <summary>Markdown version of the profile, for a file the user can review and act on.</summary>
    public string ToMarkdown(string title)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        sb.AppendLine($"{Sessions} sessions · {Turns} turns · {(Cost is null ? $"{Tokens:N0} tokens" : $"${Cost:N2}")}").AppendLine();
        sb.AppendLine("## Where the spend went").AppendLine();
        sb.AppendLine("| Outcome | Share | Turns |").AppendLine("|---|---|---|");
        foreach (var o in Outcomes) sb.AppendLine($"| {o.Outcome} | {Share(o.Outcome) * 100:0}% | {o.Turns} |");
        sb.AppendLine().AppendLine("## Proposed changes").AppendLine();
        if (Proposals.Count == 0) sb.AppendLine("Nothing stands out. Keep an eye on it as more sessions come in.");
        var n = 0;
        foreach (var p in Proposals)
            sb.AppendLine($"{++n}. **{p.Title}.** {p.Evidence}").AppendLine($"   - {p.Action}");
        return sb.ToString();
    }
}
