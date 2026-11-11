using TurnRoi.Core.Pricing;
using TurnRoi.Core.Team;

namespace TurnRoi.Cli;

/// <summary><c>turnroi export</c> and <c>turnroi team</c>: share numbers, not transcripts.</summary>
internal static class TeamCommands
{
    public static int Export(CliArgs opts, PriceTable prices)
    {
        var reports = ProfileCommands.Load(opts, prices, out var error);
        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        var person = opts.Person ?? Environment.UserName;
        var export = TeamExport.Build(person, reports, opts.HashProjects, DateTimeOffset.Now);
        var json = export.ToJson();
        if (opts.WritePath is null)
        {
            Console.WriteLine(json);
            return 0;
        }
        File.WriteAllText(opts.WritePath, json);
        Console.Error.WriteLine($"Wrote {opts.WritePath}: {export.Sessions.Count} sessions as '{person}'. It holds numbers only " +
                                "(tokens, cost, outcomes, finding counts, folder names" + (opts.HashProjects ? " hashed" : "") + ").");
        return 0;
    }

    public static int Team(CliArgs opts)
    {
        var target = opts.Target ?? ".";
        var files = Directory.Exists(target) ? Directory.EnumerateFiles(target, "*.json").Order().ToList()
            : File.Exists(target) ? [target] : [];
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"error: no export files in {target}. Each person runs: turnroi export --write <name>.json");
            return 1;
        }

        var exports = new List<TeamExport>();
        foreach (var f in files)
        {
            try { exports.Add(TeamExport.Parse(File.ReadAllText(f))); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException)
            {
                Console.Error.WriteLine(Ansi.Yellow($"skipped {Path.GetFileName(f)}: {ex.Message}"));
            }
        }
        exports = TeamRollup.Merge(exports).ToList();
        if (opts.Since is { } since)
            foreach (var e in exports) e.Sessions.RemoveAll(s => s.Started is { } st && st < since);
        if (opts.Project is { } p)
            foreach (var e in exports) e.Sessions.RemoveAll(s => !(s.Project ?? "").Contains(p, StringComparison.OrdinalIgnoreCase));

        var total = TeamRollup.Total(exports);
        if (total.Sessions == 0) { Console.Error.WriteLine("error: no sessions in the exports (check --since / --project)."); return 1; }
        var people = TeamRollup.ByPerson(exports);
        var projects = TeamRollup.ByProject(exports);

        if (opts.HtmlPath is not null)
        {
            File.WriteAllText(opts.HtmlPath, HtmlReport.Team(total, people, projects));
            Console.Error.WriteLine($"Wrote {opts.HtmlPath}");
        }

        var w = Console.Out;
        w.WriteLine();
        w.WriteLine(Ansi.Bold($"Team · {total.People} people · {total.Sessions} sessions · {ConsoleReport.Money(total.Cost)}"));
        w.WriteLine($"  Wasted {ConsoleReport.Percent(total.WastedShare)} · productive {ConsoleReport.Percent(total.ProductiveShare)} · " +
                    $"cache hit {(total.CacheHit is { } c ? ConsoleReport.Percent(c) : "-")} · re-read tokens {ConsoleReport.Compact(total.RereadTokens)}");
        Table(w, "By person", "Person", people);
        Table(w, "By project", "Project", projects);
        w.WriteLine();
        w.WriteLine(Ansi.Dim("  Compare like with like: people and projects differ in task mix. Use it to find what to fix, not to rank people."));
        return 0;
    }

    private static void Table(TextWriter w, string title, string key, IReadOnlyList<TeamRow> rows)
    {
        w.WriteLine();
        w.WriteLine(Ansi.Bold(title));
        var t = new TextTable(key, "Sessions", "Turns", "Cost", "Wasted", "Productive", "Cache hit", "Re-read tok");
        foreach (var r in rows.Take(25))
            t.Add(r.Key, r.Sessions.ToString(), r.Turns.ToString(), ConsoleReport.Money(r.Cost), ConsoleReport.Percent(r.WastedShare),
                ConsoleReport.Percent(r.ProductiveShare), r.CacheHit is { } c ? ConsoleReport.Percent(c) : "-", ConsoleReport.Compact(r.RereadTokens));
        t.Write(w, "  ");
    }
}
