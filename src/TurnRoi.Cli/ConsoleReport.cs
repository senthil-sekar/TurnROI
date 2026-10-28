using System.Text.RegularExpressions;
using TurnRoi.Core.Analysis;

namespace TurnRoi.Cli;

internal static class ConsoleReport
{
    private const int MaxFindings = 20;

    public static void Write(TextWriter w, SessionReport report, string path, int top, bool allTurns, string? priceNote)
    {
        var s = report.Session;
        var usage = s.Usage;
        var calls = s.Turns.Sum(t => t.Calls.Count);

        w.WriteLine(Ansi.Bold($"TurnROI · session {s.SessionId}") + Ansi.Dim($"  ({s.Source})"));
        w.WriteLine(Ansi.Dim(path));
        if (s.Cwd is not null) w.WriteLine(Ansi.Dim($"{s.Cwd}{(s.GitBranch is null ? "" : $" @ {s.GitBranch}")}"));
        w.WriteLine();

        // Summary
        w.WriteLine(Ansi.Bold("Summary"));
        w.WriteLine($"  Turns {s.Turns.Count(t => t.Index > 0)} · LLM calls {calls} · Models {string.Join(", ", s.Turns.SelectMany(t => t.Models).Distinct())}");
        if (usage.ContextTokens > 0)
        {
            w.WriteLine($"  Context tokens {usage.ContextTokens:N0}  " + Ansi.Dim(
                $"(cache read {Pct(usage.CacheRead, usage.ContextTokens)} · cache write {Pct(usage.CacheWrite, usage.ContextTokens)} · uncached {Pct(usage.Input, usage.ContextTokens)})"));
        }
        w.WriteLine($"  Output tokens  {usage.Output:N0}");
        w.WriteLine($"  Est. cost      {Money(report.TotalCost)}" +
            (report.UnpricedModels.Count > 0 ? Ansi.Yellow($"  unpriced models: {string.Join(", ", report.UnpricedModels)} (use --prices)") : ""));

        if (s.SkippedLines > 0)
            w.WriteLine(Ansi.Yellow($"  {s.SkippedLines} lines/rows could not be parsed (format may have changed)"));
        foreach (var note in s.Notes)
            w.WriteLine(Ansi.Dim($"  Note: {note}"));
        w.WriteLine();

        WriteOutcomes(w, report.Outcomes);

        // Turns
        var turns = report.Turns.Where(t => t.Turn.Calls.Count > 0).ToList();
        var shown = allTurns
            ? turns
            : turns.OrderByDescending(t => t.Cost ?? 0).ThenByDescending(t => t.Turn.Usage.Total).Take(top).ToList();

        w.WriteLine(Ansi.Bold(allTurns || shown.Count == turns.Count
            ? $"Turns with model calls ({turns.Count})"
            : $"Most expensive turns (top {shown.Count} of {turns.Count})"));

        var table = new TextTable("#", "Prompt", "Calls", "Tools", "Context", "Cache", "Output", "Cost", "Outcome", "Flags")
            .AlignRight(0, 2, 3, 4, 5, 6, 7);
        foreach (var t in shown)
        {
            var u = t.Turn.Usage;
            table.Add(
                t.Turn.Index.ToString(),
                Clip(t.Turn.Prompt, 38),
                t.Turn.Calls.Count.ToString(),
                t.Turn.Tools.Count.ToString(),
                Compact(u.ContextTokens),
                u.CacheHitRatio is { } r ? Percent(r) : "-",
                Compact(u.Output),
                Money(t.Cost),
                OutcomeLabel(t.Outcome),
                Flags(t));
        }
        table.Write(w, "  ");
        w.WriteLine();

        // Findings
        var findings = report.Findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.EstimatedTokens ?? 0)
            .ToList();

        w.WriteLine(Ansi.Bold($"Findings ({findings.Count})"));
        if (findings.Count == 0) w.WriteLine(Ansi.Green("  Nothing flagged."));
        foreach (var f in findings.Take(MaxFindings))
        {
            var tag = f.Severity switch
            {
                Severity.High => Ansi.Red("HIGH"),
                Severity.Warning => Ansi.Yellow("WARN"),
                _ => Ansi.Cyan("INFO"),
            };
            var tokens = f.EstimatedTokens is { } e ? Ansi.Dim($"  ~{Compact(e)} tok") : "";
            w.WriteLine($"  {tag} T{f.TurnIndex,-3} {f.Message}{tokens}");
        }
        if (findings.Count > MaxFindings)
            w.WriteLine(Ansi.Dim($"  …and {findings.Count - MaxFindings} more (use --json for all)"));
        w.WriteLine();

        w.WriteLine(Ansi.Dim("Costs are estimates from provider-reported usage. Tool-output sizes are chars/4 estimates."));
        w.WriteLine(Ansi.Dim("\"Pushed back\" is a heuristic on your next prompt, not a verdict."));
        if (priceNote is not null && report.UnpricedModels.Count > 0) w.WriteLine(Ansi.Dim(priceNote));
    }

    /// <summary>"Where the spend went": cost (or tokens when unpriced) per outcome.</summary>
    internal static void WriteOutcomes(TextWriter w, IReadOnlyList<OutcomeTotal> outcomes)
    {
        if (outcomes.Count == 0 || outcomes.All(o => o.Outcome == TurnOutcome.Unknown)) return;
        var priced = outcomes.All(o => o.Cost is not null) && outcomes.Sum(o => o.Cost ?? 0) > 0;
        var total = priced ? (double)outcomes.Sum(o => o.Cost ?? 0) : outcomes.Sum(o => o.Tokens);
        if (total <= 0) return;

        w.WriteLine(Ansi.Bold("Where the spend went") + Ansi.Dim(priced ? "" : "  (by tokens; some models unpriced)"));
        var table = new TextTable("Outcome", priced ? "Cost" : "Tokens", "Share", "Turns", "Meaning").AlignRight(1, 2, 3);
        foreach (var o in outcomes)
        {
            var amount = priced ? (double)(o.Cost ?? 0) : o.Tokens;
            table.Add(OutcomeLabel(o.Outcome), priced ? Money(o.Cost) : Compact(o.Tokens), Percent(amount / total),
                o.Turns.ToString(), OutcomeMeaning(o.Outcome));
        }
        table.Write(w, "  ");

        double Share(TurnOutcome k) => outcomes.Where(o => o.Outcome == k)
            .Sum(o => priced ? (double)(o.Cost ?? 0) : o.Tokens) / total;
        var waste = Share(TurnOutcome.Wasted) + Share(TurnOutcome.Failing);
        if (waste >= 0.25)
            w.WriteLine(Ansi.Red($"  {Percent(waste)} of spend went to turns that were pushed back on or left tests failing."));
        w.WriteLine();
    }

    internal static string OutcomeLabel(TurnOutcome o) => o switch
    {
        TurnOutcome.Productive => "productive",
        TurnOutcome.Unverified => "unverified",
        TurnOutcome.Failing => "failing",
        TurnOutcome.Wasted => "wasted",
        TurnOutcome.Exploratory => "exploratory",
        _ => "unknown",
    };

    private static string OutcomeMeaning(TurnOutcome o) => o switch
    {
        TurnOutcome.Productive => "changed code; tests passed or committed",
        TurnOutcome.Unverified => "changed code; no test or commit seen",
        TurnOutcome.Failing => "changed code; last test run failed",
        TurnOutcome.Wasted => "your next prompt pushed back",
        TurnOutcome.Exploratory => "read/answered; no code changed",
        _ => "not enough data in this source",
    };

    private static string Flags(TurnReport t)
    {
        var kinds = t.Findings.Select(f => f.Kind).ToHashSet();
        var flags = "";
        if (kinds.Contains(FindingKind.RepeatedRead)) flags += "R";
        if (kinds.Contains(FindingKind.OversizedToolResult)) flags += "O";
        if (kinds.Contains(FindingKind.LowCacheHit)) flags += "C";
        if (kinds.Contains(FindingKind.CorrectedNextTurn)) flags += "X";
        if (kinds.Contains(FindingKind.ToolErrors)) flags += "E";
        return flags;
    }

    private static string Pct(long part, long whole) => whole == 0 ? "-" : Percent((double)part / whole);

    internal static string Percent(double ratio) => $"{ratio * 100:0}%";

    internal static string Money(decimal? v) => v is null ? "n/a" : v < 0.01m && v > 0 ? "<$0.01" : $"${v:N2}";

    internal static string Compact(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.0}M",
        >= 10_000 => $"{n / 1_000.0:0}k",
        >= 1_000 => $"{n / 1_000.0:0.0}k",
        _ => n.ToString(),
    };

    private static string Clip(string s, int max)
    {
        var one = Regex.Replace(s, @"\s+", " ").Trim();
        return one.Length <= max ? one : one[..(max - 1)] + "…";
    }
}
