using System.Text.RegularExpressions;
using TurnRoi.Core.Model;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Analysis;

public enum FindingKind
{
    RepeatedRead,
    OversizedToolResult,
    LowCacheHit,
    CorrectedNextTurn,
    ToolErrors,
}

public enum Severity { Info, Warning, High }

/// <summary>A single, evidence-backed observation about wasted or questionable spend.</summary>
public sealed record Finding(
    FindingKind Kind,
    Severity Severity,
    int? TurnIndex,
    string Message,
    long? EstimatedTokens = null,
    string? Subject = null);

public sealed record TurnReport(Turn Turn, decimal? Cost, IReadOnlyList<Finding> Findings,
    TurnOutcome Outcome = TurnOutcome.Unknown, OutcomeEvidence? Evidence = null)
{
    public bool WasCorrected => Findings.Any(f => f.Kind == FindingKind.CorrectedNextTurn);
}

public sealed record SessionReport(
    AgentSession Session,
    IReadOnlyList<TurnReport> Turns,
    IReadOnlyList<Finding> Findings,
    decimal? TotalCost,
    IReadOnlyList<string> UnpricedModels,
    IReadOnlyList<OutcomeTotal> Outcomes)
{
    /// <summary>Cost of turns the human immediately corrected or interrupted. A first, rough proxy for "not worth it".</summary>
    public decimal? CorrectedSpend =>
        TotalCost is null || !Session.HasPrompts ? null : Turns.Where(t => t.WasCorrected).Sum(t => t.Cost ?? 0);
}

public sealed class AnalyzerOptions
{
    /// <summary>Tool results above this estimated token count are flagged.</summary>
    public long OversizedResultTokens { get; init; } = 5_000;

    /// <summary>Turns whose context is at least this large are checked for cache efficiency.</summary>
    public long CacheCheckMinContextTokens { get; init; } = 20_000;

    public double LowCacheHitRatio { get; init; } = 0.5;

    public int ToolErrorThreshold { get; init; } = 3;
}

/// <summary>
/// Turns a normalized session into per-turn cost and a list of waste findings.
/// Every finding is a heuristic with the evidence attached, never a verdict.
/// </summary>
public sealed class SessionAnalyzer
{
    // Matches the start of a human reply that pushes back on the previous turn.
    private static readonly Regex CorrectionPattern = new(
        @"^\s*(\[Request interrupted by user|no\b|nope\b|wrong\b|incorrect\b|revert\b|undo\b|stop\b|that'?s (not|wrong)|this is (not|wrong)|that (didn'?t|did not|doesn'?t) work|(it'?s |it is )?still (not|broken|failing|wrong)|you (broke|missed|forgot)|why did you)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly PriceTable _prices;
    private readonly AnalyzerOptions _options;
    private readonly IGitHistory? _git;

    /// <param name="git">Optional git history lookup so edits committed after the session count as kept.</param>
    public SessionAnalyzer(PriceTable prices, AnalyzerOptions? options = null, IGitHistory? git = null)
    {
        _prices = prices;
        _options = options ?? new AnalyzerOptions();
        _git = git;
    }

    public static bool IsCorrection(string prompt) => CorrectionPattern.IsMatch(prompt);

    public SessionReport Analyze(AgentSession session)
    {
        var perTurn = session.Turns.ToDictionary(t => t.Index, _ => new List<Finding>());
        var sessionFindings = new List<Finding>();

        FindRepeatedReads(session, perTurn);
        FindOversizedResults(session, perTurn);
        FindLowCacheHits(session, perTurn);
        FindCorrections(session, perTurn);
        FindToolErrors(session, perTurn);

        var pushedBack = perTurn.Where(kv => kv.Value.Any(f => f.Kind == FindingKind.CorrectedNextTurn))
            .Select(kv => kv.Key).ToHashSet();
        var outcomes = OutcomeClassifier.Classify(session, pushedBack, _git);

        var turnReports = session.Turns
            .Select(t => new TurnReport(t, _prices.Cost(t.Calls), perTurn[t.Index], outcomes[t.Index].Outcome, outcomes[t.Index].Evidence))
            .ToList();

        var allCalls = session.Turns.SelectMany(t => t.Calls).ToList();
        var unpriced = _prices.UnpricedModels(allCalls).ToList();
        var total = _prices.Cost(allCalls);

        sessionFindings.AddRange(turnReports.SelectMany(t => t.Findings));

        return new SessionReport(session, turnReports, sessionFindings, total, unpriced, OutcomeClassifier.Summarize(turnReports));
    }

    private static void FindRepeatedReads(AgentSession session, Dictionary<int, List<Finding>> perTurn)
    {
        // A re-read only counts as waste when the same part of the file is read again with no edit in between.
        var readsSinceLastWrite = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var turn in session.Turns)
        foreach (var tool in turn.Tools)
        {
            if (tool.Target is null) continue;

            if (ToolKinds.IsEdit(tool))
            {
                foreach (var key in readsSinceLastWrite.Keys.Where(k => k == tool.Target || k.StartsWith(tool.Target + "#", StringComparison.Ordinal)).ToList())
                    readsSinceLastWrite.Remove(key);
                continue;
            }

            if (!ToolKinds.IsRead(tool)) continue;

            var readKey = tool.Range is null ? tool.Target : $"{tool.Target}#{tool.Range}";
            readsSinceLastWrite.TryGetValue(readKey, out var count);
            readsSinceLastWrite[readKey] = ++count;

            if (count >= 2)
            {
                perTurn[turn.Index].Add(new Finding(
                    FindingKind.RepeatedRead, Severity.Warning, turn.Index,
                    $"Re-read of unchanged file {ShortPath(tool.Target)} (read #{count} since last edit)",
                    tool.EstimatedResultTokens, tool.Target));
            }
        }
    }

    private void FindOversizedResults(AgentSession session, Dictionary<int, List<Finding>> perTurn)
    {
        foreach (var turn in session.Turns)
        foreach (var tool in turn.Tools)
        {
            if (tool.EstimatedResultTokens is not { } est || est < _options.OversizedResultTokens) continue;
            var target = tool.Target is null ? "" : $" ({Truncate(tool.Target, 60)})";
            perTurn[turn.Index].Add(new Finding(
                FindingKind.OversizedToolResult, est >= _options.OversizedResultTokens * 4 ? Severity.High : Severity.Warning,
                turn.Index, $"{tool.Name}{target} returned ~{est:N0} tokens", est,
                ToolKinds.IsShell(tool) && tool.Target is not null ? ToolKinds.CommandLabel(tool.Target) : tool.Target));
        }
    }

    private void FindLowCacheHits(AgentSession session, Dictionary<int, List<Finding>> perTurn)
    {
        // The first turn always pays to write the cache, so start from the second.
        foreach (var turn in session.Turns.Skip(1))
        {
            var u = turn.Usage;
            if (u.ContextTokens < _options.CacheCheckMinContextTokens) continue;
            if (u.CacheHitRatio is not { } ratio || ratio >= _options.LowCacheHitRatio) continue;
            perTurn[turn.Index].Add(new Finding(
                FindingKind.LowCacheHit, Severity.Warning, turn.Index,
                $"Only {ratio * 100:0}% of {u.ContextTokens:N0} context tokens came from cache",
                u.Input + u.CacheWrite));
        }
    }

    private static void FindCorrections(AgentSession session, Dictionary<int, List<Finding>> perTurn)
    {
        if (!session.HasPrompts) return;
        for (var i = 0; i < session.Turns.Count - 1; i++)
        {
            var next = session.Turns[i + 1];
            if (!IsCorrection(next.Prompt)) continue;
            var turn = session.Turns[i];
            perTurn[turn.Index].Add(new Finding(
                FindingKind.CorrectedNextTurn, Severity.High, turn.Index,
                $"Next prompt pushed back: \"{Truncate(OneLine(next.Prompt), 50)}\"",
                turn.Usage.Total));
        }
    }

    private void FindToolErrors(AgentSession session, Dictionary<int, List<Finding>> perTurn)
    {
        foreach (var turn in session.Turns)
        {
            var errors = turn.Tools.Count(t => t.IsError);
            if (errors < _options.ToolErrorThreshold) continue;
            perTurn[turn.Index].Add(new Finding(
                FindingKind.ToolErrors, Severity.Warning, turn.Index,
                $"{errors} failed tool calls in this turn"));
        }
    }

    internal static string ShortPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? path : ".../" + string.Join('/', parts[^2..]);
    }

    internal static string OneLine(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
