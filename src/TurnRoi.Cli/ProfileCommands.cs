using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Apply;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Cli;

/// <summary><c>turnroi profile</c> (waste patterns across sessions + proposals) and <c>turnroi compare</c> (before/after).</summary>
internal static class ProfileCommands
{
    public static int Profile(CliArgs opts, PriceTable prices)
    {
        var projectDir = Path.GetFullPath(opts.ProjectDir ?? Directory.GetCurrentDirectory());
        if (opts.Undo) return Undo(projectDir);

        // When applying, learn only from sessions that ran in this project (unless --project says otherwise).
        var explicitFile = opts.Target is { } targetFile && File.Exists(targetFile);
        var reports = Load(opts, prices, out var error, opts.Apply && opts.Project is null && !explicitFile ? projectDir : null);
        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        var profile = WasteProfile.Build(reports);
        var w = Console.Out;
        var scope = opts.Project is null ? "all projects" : $"projects matching '{opts.Project}'";

        w.WriteLine(Ansi.Bold($"TurnROI profile · {profile.Sessions} sessions · {scope}"));
        w.WriteLine($"  Turns {profile.Turns} · Tokens {profile.Tokens:N0} · Est. cost {ConsoleReport.Money(profile.Cost)}" +
                    (profile.CacheHitRatio is { } c ? $" · Cache hit {ConsoleReport.Percent(c)}" : ""));
        w.WriteLine();

        ConsoleReport.WriteOutcomes(w, profile.Outcomes);

        if (profile.NoisyCommands.Count > 0)
        {
            w.WriteLine(Ansi.Bold("Noisiest commands"));
            var t = new TextTable("Command", "Runs", "Output tokens").AlignRight(1, 2);
            foreach (var n in profile.NoisyCommands) t.Add(n.Command, n.Runs.ToString(), $"~{n.Tokens:N0}");
            t.Write(w, "  ");
            w.WriteLine();
        }

        if (profile.RepeatedFiles.Count > 0)
        {
            w.WriteLine(Ansi.Bold("Files re-read without changes"));
            var t = new TextTable("File", "Re-reads", "Sessions", "Tokens").AlignRight(1, 2, 3);
            foreach (var f in profile.RepeatedFiles) t.Add(f.File, f.Rereads.ToString(), f.Sessions.ToString(), $"~{f.Tokens:N0}");
            t.Write(w, "  ");
            w.WriteLine();
        }

        w.WriteLine(Ansi.Bold($"Proposed changes ({profile.Proposals.Count})"));
        if (profile.Proposals.Count == 0) w.WriteLine(Ansi.Green("  Nothing stands out yet."));
        var i = 0;
        foreach (var p in profile.Proposals)
        {
            w.WriteLine($"  {++i}. {Ansi.Bold(p.Title)}  " + Ansi.Dim(p.Evidence));
            w.WriteLine($"     {p.Action}");
        }

        if (opts.WritePath is not null)
        {
            File.WriteAllText(opts.WritePath, profile.ToMarkdown($"TurnROI waste profile ({scope})"));
            w.WriteLine();
            w.WriteLine($"Wrote {opts.WritePath}.");
        }

        if (opts.Apply) return ApplyGuidance(opts, profile, projectDir);
        if (profile.Proposals.Count > 0)
        {
            w.WriteLine();
            w.WriteLine(Ansi.Dim("Turn these into rules, a skill and hooks for your agent: turnroi profile --apply --agent claude|cursor|copilot|all"));
        }
        return 0;
    }

    private static int ApplyGuidance(CliArgs opts, WasteProfile profile, string projectDir)
    {
        var w = Console.Out;
        var agents = opts.Agents.Count > 0 ? opts.Agents : DefaultAgent(opts) is { } a ? [a] : null;
        if (agents is null)
        {
            Console.Error.WriteLine("error: choose which agent(s) to write for: --agent claude|cursor|copilot|all");
            return 2;
        }

        var guidance = RepoGuidance.Build(profile, projectDir, commit: GitCliHistory.Head(projectDir));
        var (changes, skipped) = ApplyPlanner.Plan(projectDir, guidance,
            new ApplyOptions(agents, HookCommand: opts.HookCommand ?? "turnroi", SharedClaudeSettings: opts.Shared));

        w.WriteLine();
        w.WriteLine(Ansi.Bold($"Planned changes for {string.Join(", ", agents)} in {projectDir}"));
        foreach (var s in skipped) w.WriteLine(Ansi.Yellow($"  skipped {s}"));
        if (changes.Count == 0)
        {
            w.WriteLine("  Nothing to change: everything is already up to date.");
            return 0;
        }

        foreach (var c in changes)
        {
            var rel = Path.GetRelativePath(projectDir, c.Path);
            w.WriteLine();
            w.WriteLine(Ansi.Bold($"  {(c.IsCreate ? "create" : c.IsDelete ? "delete" : "update")} {rel}") + Ansi.Dim($"  ({c.Description})"));
            foreach (var line in Preview(c).Split('\n').Take(40)) w.WriteLine(Ansi.Dim("  │ ") + line);
        }
        w.WriteLine();

        if (!opts.Yes)
        {
            if (Console.IsInputRedirected)
            {
                w.WriteLine("Preview only. Re-run with --yes to write these files.");
                return 0;
            }
            w.Write("Write these files? [y/N] ");
            var answer = Console.ReadLine()?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                w.WriteLine("Nothing written.");
                return 0;
            }
        }

        ApplyRunner.Apply(projectDir, changes);
        w.WriteLine(Ansi.Green($"Wrote {changes.Count} file(s). Undo with: turnroi profile --undo"));
        w.WriteLine("Restart the agent so it picks up the new rules, skill and hooks. Check the effect later with: turnroi compare --split <today>");
        return 0;
    }

    /// <summary>What to show for a change: the new TurnROI block for shared files, the new file otherwise.</summary>
    private static string Preview(FileChange c)
    {
        if (c.After is null) return "(file removed)";
        var start = c.After.IndexOf(ApplyPlanner.BlockStart, StringComparison.Ordinal);
        var end = c.After.IndexOf(ApplyPlanner.BlockEnd, StringComparison.Ordinal);
        return start >= 0 && end > start
            ? c.After[(start + ApplyPlanner.BlockStart.Length)..end].Trim('\n')
            : c.After.TrimEnd('\n');
    }

    private static TargetAgent? DefaultAgent(CliArgs opts) => opts.Source switch
    {
        null or AgentSource.Claude => TargetAgent.Claude,
        AgentSource.Copilot => TargetAgent.Copilot,
        AgentSource.Cursor => TargetAgent.Cursor,
        _ => null,
    };

    private static int Undo(string projectDir)
    {
        var restored = ApplyRunner.Undo(projectDir);
        if (restored is null)
        {
            Console.WriteLine("Nothing to undo.");
            return 0;
        }
        foreach (var p in restored) Console.WriteLine($"restored {p}");
        Console.WriteLine(Ansi.Green("Undid the last TurnROI change."));
        return 0;
    }

    public static int Compare(CliArgs opts, PriceTable prices)
    {
        if (opts.Split is not { } split)
        {
            Console.Error.WriteLine("error: compare needs --split <date>, e.g. the day you installed the hooks.");
            return 2;
        }

        var reports = Load(opts, prices, out var error);
        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        var before = reports.Where(r => Start(r) < split).ToList();
        var after = reports.Where(r => Start(r) >= split).ToList();
        if (before.Count == 0 || after.Count == 0)
        {
            Console.Error.WriteLine($"error: need sessions on both sides of {split:yyyy-MM-dd} (found {before.Count} before, {after.Count} after).");
            return 1;
        }

        var a = WasteProfile.Build(before);
        var b = WasteProfile.Build(after);
        var w = Console.Out;
        w.WriteLine(Ansi.Bold($"TurnROI compare · before vs after {split:yyyy-MM-dd}"));
        w.WriteLine();

        var t = new TextTable("Metric", "Before", "After", "Change").AlignRight(1, 2, 3);
        t.Add("Sessions", a.Sessions.ToString(), b.Sessions.ToString(), "");
        t.Add("Turns", a.Turns.ToString(), b.Turns.ToString(), "");
        if (a.CostPerTurn is { } ca && b.CostPerTurn is { } cb)
            t.Add("Cost per turn", ConsoleReport.Money(ca), ConsoleReport.Money(cb), Delta((double)ca, (double)cb));
        t.Add("Tokens per turn", ConsoleReport.Compact(a.TokensPerTurn), ConsoleReport.Compact(b.TokensPerTurn), Delta(a.TokensPerTurn, b.TokensPerTurn));
        foreach (var o in new[] { TurnOutcome.Productive, TurnOutcome.Wasted, TurnOutcome.Failing, TurnOutcome.Unverified })
            t.Add($"{ConsoleReport.OutcomeLabel(o)} share", ConsoleReport.Percent(a.Share(o)), ConsoleReport.Percent(b.Share(o)), Points(a.Share(o), b.Share(o)));
        if (a.CacheHitRatio is { } ha && b.CacheHitRatio is { } hb)
            t.Add("Cache hit", ConsoleReport.Percent(ha), ConsoleReport.Percent(hb), Points(ha, hb));
        t.Write(w, "  ");
        w.WriteLine();
        w.WriteLine(Ansi.Dim("Different weeks bring different tasks: treat small samples and small changes as noise."));
        return 0;
    }

    private static List<SessionReport> Load(CliArgs opts, PriceTable prices, out string? error, string? underDir = null)
    {
        error = null;
        var source = opts.Source ?? (opts.Target is { } f && File.Exists(f) ? SessionSources.Detect(f) : AgentSource.Claude);
        IReadOnlyList<FileInfo> files = opts.Target is { } t && File.Exists(t)
            ? [new FileInfo(t)]
            : SessionSources.FindSessions(source, opts.Root, opts.LimitSet ? opts.Limit : 100);

        if (files.Count == 0)
        {
            error = source == AgentSource.Cursor
                ? "error: pass the Cursor usage CSV: turnroi profile <usage.csv>"
                : $"error: no {source} sessions found.";
            return [];
        }

        var analyzer = new SessionAnalyzer(prices, git: opts.NoGit ? null : new GitCliHistory());
        var adapter = SessionSources.AdapterFor(source);
        var reports = new List<SessionReport>();
        foreach (var file in files)
        {
            try
            {
                var session = adapter.Read(file.FullName);
                if (opts.Project is { } p && !(session.Cwd ?? "").Contains(p, StringComparison.OrdinalIgnoreCase)) continue;
                if (underDir is not null && session.Cwd is { } cwd && !IsUnder(cwd, underDir)) continue;
                var report = analyzer.Analyze(session);
                if (opts.Since is { } since && Start(report) < since) continue;
                if (report.Turns.Any(x => x.Turn.Calls.Count > 0)) reports.Add(report);
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(Ansi.Yellow($"skipped {file.Name}: {ex.Message}"));
            }
        }

        if (reports.Count == 0) error = "error: no sessions matched (check --project / --since).";
        return reports;
    }

    private static bool IsUnder(string path, string dir)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return p.Equals(d, cmp) || p.StartsWith(d + Path.DirectorySeparatorChar, cmp);
    }

    private static DateTimeOffset Start(SessionReport r) =>
        r.Session.Turns.Select(t => t.StartedAt).OfType<DateTimeOffset>().DefaultIfEmpty(DateTimeOffset.MinValue).Min();

    private static string Delta(double before, double after) =>
        before == 0 ? "" : $"{(after - before) / before * 100:+0;-0;0}%";

    private static string Points(double before, double after) => $"{(after - before) * 100:+0;-0;0} pts";
}
