using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Evaluation;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Cli;

/// <summary><c>turnroi label</c> and <c>turnroi eval</c>: check TurnROI's outcome calls against people's.</summary>
internal static class LabelCommands
{
    public static int Label(CliArgs opts, PriceTable prices)
    {
        var target = opts.Target ?? "latest";
        var source = opts.Source ?? (File.Exists(target) ? SessionSources.Detect(target) : AgentSource.Claude);
        var found = SessionSources.Resolve(source, target, opts.Root);
        if (found is null) { Console.Error.WriteLine($"error: no {SessionSources.Label(source)} session matching '{target}'."); return 1; }

        SessionReport report;
        try { report = new SessionAnalyzer(prices, git: opts.NoGit ? null : new GitCliHistory()).Analyze(SessionSources.Read(found, opts.UsageCsv)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or FormatException or NotSupportedException)
        {
            Console.Error.WriteLine($"error: could not read {found.Location}: {ex.Message}");
            return 1;
        }
        if (!report.Session.HasPrompts) { Console.Error.WriteLine("error: this source has no prompts to label turns by."); return 1; }

        var turns = report.Turns.Where(t => t.Turn.Index > 0).ToList();
        TurnLabel Make(TurnReport t, TurnOutcome label) => new()
        {
            At = DateTimeOffset.Now, Source = SessionSources.Label(found.Source), Session = found.Id,
            Location = Path.GetFullPath(found.Location), Turn = t.Turn.Index, Label = label.ToString(), Predicted = t.Outcome.ToString(),
        };

        var saved = 0;
        if (opts.SetLabels is { } spec)
        {
            foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2 || !int.TryParse(kv[0], out var idx) || LabelStore.Outcome(kv[1]) is not { } o)
                { Console.Error.WriteLine($"error: bad label '{part}' (expected <turn>=<outcome>, e.g. 3=wasted)"); return 2; }
                if (turns.FirstOrDefault(t => t.Turn.Index == idx) is not { } t)
                { Console.Error.WriteLine($"error: no turn {idx} in this session"); return 2; }
                LabelStore.Append(Make(t, o), opts.LabelsFile);
                saved++;
            }
            Console.WriteLine($"Saved {saved} label(s) to {opts.LabelsFile ?? LabelStore.DefaultPath}");
            return 0;
        }

        if (Console.IsInputRedirected) { Console.Error.WriteLine("error: label asks turn by turn; without a terminal use --set 3=wasted,4=productive"); return 2; }

        var w = Console.Out;
        w.WriteLine(Ansi.Bold($"Labelling {found.Id} · {turns.Count} turns"));
        w.WriteLine(Ansi.Dim("  Enter = agree with TurnROI · p productive · u unverified · f failing · w wasted · e exploratory · s skip · q quit"));
        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            var next = i + 1 < turns.Count ? turns[i + 1].Turn.Prompt : null;
            w.WriteLine();
            w.WriteLine(Ansi.Bold($"Turn {t.Turn.Index}") + $"  {ConsoleReport.Money(t.Cost)} · {t.Turn.Tools.Count} tools · " +
                        $"{t.Turn.Tools.Count(ToolKinds.IsEdit)} edits · TurnROI says {Ansi.Cyan(ConsoleReport.OutcomeLabel(t.Outcome))}");
            w.WriteLine($"  you:  {Clip(t.Turn.Prompt, 160)}");
            if (next is not null) w.WriteLine(Ansi.Dim($"  next: {Clip(next, 160)}"));
            w.Write("  > ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is null or "q") break;
            if (answer == "s") continue;
            var label = answer.Length == 0 ? t.Outcome : LabelStore.Outcome(answer);
            if (label is null or TurnOutcome.Unknown) { w.WriteLine(Ansi.Yellow("  skipped (unrecognised)")); continue; }
            LabelStore.Append(Make(t, label.Value), opts.LabelsFile);
            saved++;
        }
        w.WriteLine();
        w.WriteLine($"Saved {saved} label(s). See how TurnROI scores against them: turnroi eval");
        return 0;
    }

    public static int Eval(CliArgs opts, PriceTable prices)
    {
        var labels = LabelStore.Read(opts.LabelsFile);
        if (labels.Count == 0) { Console.Error.WriteLine("error: no labels yet. Label some turns with: turnroi label"); return 1; }

        // Re-run today's detector where the session is still readable; else use what was predicted at label time.
        var analyzer = new SessionAnalyzer(prices, git: opts.NoGit ? null : new GitCliHistory());
        var pairs = new List<(TurnOutcome, TurnOutcome)>();
        var stale = 0;
        foreach (var group in labels.GroupBy(l => (l.Source, l.Session, l.Location)))
        {
            Dictionary<int, TurnOutcome>? now = null;
            if (SessionSources.Parse(group.Key.Source) is { } src && (File.Exists(group.Key.Location) || Directory.Exists(group.Key.Location)))
            {
                try
                {
                    var r = analyzer.Analyze(SessionSources.Read(new SessionRef(src, group.Key.Session, group.Key.Location, DateTimeOffset.MinValue)));
                    now = r.Turns.ToDictionary(t => t.Turn.Index, t => t.Outcome);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or FormatException or NotSupportedException) { }
            }
            foreach (var l in group)
            {
                var actual = LabelStore.Outcome(l.Label)!.Value;
                TurnOutcome predicted;
                if (now is not null && now.TryGetValue(l.Turn, out var p)) predicted = p;
                else
                {
                    stale++;
                    predicted = Enum.TryParse<TurnOutcome>(l.Predicted, out var old) ? old : TurnOutcome.Unknown;
                }
                pairs.Add((actual, predicted));
            }
        }

        var result = Evaluator.Score(pairs);
        var w = Console.Out;
        w.WriteLine();
        w.WriteLine(Ansi.Bold($"TurnROI vs your labels · {result.Total} turns · accuracy {ConsoleReport.Percent(result.Accuracy)}"));
        if (stale > 0) w.WriteLine(Ansi.Dim($"  {stale} label(s) scored against the prediction saved at label time (session no longer readable)."));

        w.WriteLine();
        var t = new TextTable("Outcome", "Labelled", "Predicted", "Precision", "Recall", "F1");
        foreach (var c in result.Classes)
            t.Add(ConsoleReport.OutcomeLabel(c.Outcome), c.Support.ToString(), c.Predicted.ToString(), Pct(c.Precision), Pct(c.Recall), Pct(c.F1));
        t.Write(w, "  ");

        var shown = Evaluator.Classes.Where(c => result.Classes.Any(x => x.Outcome == c)).ToList();
        w.WriteLine();
        w.WriteLine(Ansi.Bold("Confusion (rows: your label, columns: TurnROI)"));
        var m = new TextTable(["", .. shown.Select(ConsoleReport.OutcomeLabel)]);
        foreach (var a in shown)
            m.Add([ConsoleReport.OutcomeLabel(a), .. shown.Select(p => result.Confusion.GetValueOrDefault((a, p)) is var n and > 0 ? n.ToString() : "·")]);
        m.Write(w, "  ");

        var wasted = result.Classes.FirstOrDefault(c => c.Outcome == TurnOutcome.Wasted);
        if (wasted is not null && result.Total >= 20)
        {
            w.WriteLine();
            w.WriteLine(wasted.Recall < 0.6
                ? Ansi.Yellow($"  Pushback detection misses {ConsoleReport.Percent(1 - (wasted.Recall ?? 0))} of the turns you call wasted: open an issue with example phrasings (no code).")
                : $"  Pushback detection finds {ConsoleReport.Percent(wasted.Recall ?? 0)} of wasted turns at {Pct(wasted.Precision)} precision.");
        }
        return 0;
    }

    private static string Pct(double? v) => v is { } d ? ConsoleReport.Percent(d) : "-";

    private static string Clip(string s, int max)
    {
        var one = string.Join(' ', s.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= max ? one : one[..(max - 1)] + "…";
    }
}
