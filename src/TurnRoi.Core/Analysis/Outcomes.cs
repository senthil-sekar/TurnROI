using System.Diagnostics;
using TurnRoi.Core.Model;

namespace TurnRoi.Core.Analysis;

/// <summary>What a turn's spend bought. The ROI half of TurnROI.</summary>
public enum TurnOutcome
{
    /// <summary>Changed code and the change was verified: tests passed, or it was committed.</summary>
    Productive,

    /// <summary>Changed code, but nothing shows whether it worked.</summary>
    Unverified,

    /// <summary>Changed code and the last test run in the turn failed.</summary>
    Failing,

    /// <summary>The human's next prompt pushed back ("no", "revert", interrupted).</summary>
    Wasted,

    /// <summary>Read, searched or answered without changing code.</summary>
    Exploratory,

    /// <summary>The source doesn't record enough to judge (e.g. Cursor's usage export).</summary>
    Unknown,
}

public sealed record OutcomeEvidence(
    int Edits,
    IReadOnlyList<string> EditedFiles,
    int TestRuns,
    bool? LastTestPassed,
    bool CommittedInTurn,
    int FilesCommittedLater,
    bool PushedBack);

/// <summary>Spend attributed to one outcome across a session (or many sessions).</summary>
public sealed record OutcomeTotal(TurnOutcome Outcome, int Turns, decimal? Cost, long Tokens);

/// <summary>Looks up which files were committed after a point in time. Lets "unverified" edits count as kept.</summary>
public interface IGitHistory
{
    /// <summary>Commits since <paramref name="since"/> as (commit time, absolute file paths). Empty when not a repo.</summary>
    IReadOnlyList<(DateTimeOffset At, IReadOnlySet<string> Files)> CommitsSince(string directory, DateTimeOffset since);
}

/// <summary>Reads history with the git CLI. Any failure (no git, not a repo) yields no commits.</summary>
public sealed class GitCliHistory : IGitHistory
{
    public IReadOnlyList<(DateTimeOffset At, IReadOnlySet<string> Files)> CommitsSince(string directory, DateTimeOffset since)
    {
        if (!Directory.Exists(directory)) return [];
        var root = Run(directory, "rev-parse", "--show-toplevel")?.Trim();
        if (string.IsNullOrEmpty(root)) return [];

        var log = Run(directory, "log", "--all", $"--since=@{since.ToUnixTimeSeconds()}", "--name-only", "--pretty=format:@@%ct");
        if (log is null) return [];

        var commits = new List<(DateTimeOffset, IReadOnlySet<string>)>();
        DateTimeOffset? at = null;
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (at is not null) commits.Add((at.Value, files));
                at = long.TryParse(line[2..], out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs) : null;
                files = new HashSet<string>(StringComparer.Ordinal);
            }
            else if (line.Length > 0)
            {
                files.Add(Path.GetFullPath(Path.Combine(root, line)));
            }
        }
        if (at is not null) commits.Add((at.Value, files));
        return commits;
    }

    /// <summary>Short commit id of HEAD, or null when not a git repo.</summary>
    public static string? Head(string directory) =>
        Directory.Exists(directory) ? Run(directory, "rev-parse", "--short", "HEAD")?.Trim() is { Length: > 0 } sha ? sha : null : null;

    private static string? Run(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(dir);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>Classifies each turn's outcome from what the agent did and what happened next.</summary>
public static class OutcomeClassifier
{
    public static IReadOnlyDictionary<int, (TurnOutcome Outcome, OutcomeEvidence Evidence)> Classify(
        AgentSession session, IReadOnlySet<int> pushedBackTurns, IGitHistory? git)
    {
        var commits = LoadCommits(session, git);
        var result = new Dictionary<int, (TurnOutcome, OutcomeEvidence)>();

        foreach (var turn in session.Turns)
        {
            var edits = turn.Tools.Where(ToolKinds.IsEdit).ToList();
            var editedFiles = edits.Select(t => t.Target).OfType<string>()
                .Select(f => Normalize(f, session.Cwd)).Distinct(StringComparer.Ordinal).ToList();
            var tests = turn.Tools.Where(ToolKinds.IsTestRun).ToList();
            var lastTest = tests.Count == 0 ? null : ToolKinds.TestPassedResult(tests[^1]);
            var committed = turn.Tools.Any(ToolKinds.IsCommit);
            var keptLater = turn.StartedAt is { } start
                ? editedFiles.Count(f => commits.Any(c => c.At >= start && c.Files.Contains(f)))
                : 0;
            var pushedBack = pushedBackTurns.Contains(turn.Index);

            var evidence = new OutcomeEvidence(edits.Count, editedFiles, tests.Count, lastTest, committed, keptLater, pushedBack);
            result[turn.Index] = (Decide(session, turn, evidence), evidence);
        }
        return result;
    }

    private static TurnOutcome Decide(AgentSession session, Turn turn, OutcomeEvidence e)
    {
        if (turn.Tools.Count == 0 && !session.HasPrompts) return TurnOutcome.Unknown;
        if (e.PushedBack) return TurnOutcome.Wasted;

        if (e.Edits > 0)
        {
            if (e.LastTestPassed == false) return TurnOutcome.Failing;
            if (e.LastTestPassed == true || e.CommittedInTurn || e.FilesCommittedLater > 0) return TurnOutcome.Productive;
            return TurnOutcome.Unverified;
        }

        if (e.CommittedInTurn || e.LastTestPassed == true) return TurnOutcome.Productive;
        return TurnOutcome.Exploratory;
    }

    private static List<(DateTimeOffset At, IReadOnlySet<string> Files)> LoadCommits(AgentSession session, IGitHistory? git)
    {
        if (git is null || session.Cwd is null) return [];
        var start = session.Turns.Select(t => t.StartedAt).OfType<DateTimeOffset>().DefaultIfEmpty().Min();
        if (start == default) return [];
        // Only worth a git call when something was edited.
        if (!session.Turns.Any(t => t.Tools.Any(ToolKinds.IsEdit))) return [];
        return git.CommitsSince(session.Cwd, start).ToList();
    }

    private static string Normalize(string path, string? cwd)
    {
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(path) || cwd is null ? path : Path.Combine(cwd, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>Totals per outcome, in a fixed order; turns without model calls are left out.</summary>
    public static IReadOnlyList<OutcomeTotal> Summarize(IEnumerable<TurnReport> turns)
    {
        var withCalls = turns.Where(t => t.Turn.Calls.Count > 0).ToList();
        return Enum.GetValues<TurnOutcome>()
            .Select(o =>
            {
                var group = withCalls.Where(t => t.Outcome == o).ToList();
                decimal? cost = group.Any(t => t.Cost is null) ? null : group.Sum(t => t.Cost ?? 0);
                return new OutcomeTotal(o, group.Count, cost, group.Sum(t => t.Turn.Usage.Total));
            })
            .Where(t => t.Turns > 0)
            .ToList();
    }
}
