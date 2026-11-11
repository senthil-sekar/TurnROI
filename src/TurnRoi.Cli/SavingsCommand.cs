using System.Text.Json.Nodes;
using TurnRoi.Core.Guardrails;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Cli;

/// <summary><c>turnroi savings</c>: what the guardrails did, from the intervention log the hooks write.</summary>
internal static class SavingsCommand
{
    public static int Run(CliArgs opts, PriceTable prices)
    {
        var records = InterventionLog.Read(opts.Target is { } f && File.Exists(f) ? f : null)
            .Where(r => opts.Since is not { } since || r.At >= since)
            .Where(r => opts.Project is not { } p || (r.Project ?? "").Contains(p, StringComparison.OrdinalIgnoreCase))
            .Where(r => opts.Agents.Count == 0 || opts.Agents.Any(a => a.ToString().Equals(r.Agent, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var summary = SavingsReport.Summarize(records, prices);
        if (opts.HtmlPath is not null)
        {
            File.WriteAllText(opts.HtmlPath, HtmlReport.Savings(summary, records.Count, records.Select(r => r.Session).Distinct().Count()));
            Console.Error.WriteLine($"Wrote {opts.HtmlPath}");
        }

        if (opts.Json)
        {
            var arr = new JsonArray();
            foreach (var s in summary)
                arr.Add((JsonNode)new JsonObject
                {
                    ["rule"] = s.Rule, ["action"] = s.Action, ["count"] = s.Count, ["sessions"] = s.Sessions,
                    ["tokensAvoided"] = s.TokensAvoided, ["costAvoided"] = s.CostAvoided is { } c ? JsonValue.Create(c) : null,
                });
            Console.WriteLine(new JsonObject { ["interventions"] = records.Count, ["byRule"] = arr }
                .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var w = Console.Out;
        w.WriteLine(Ansi.Bold("TurnROI savings") + Ansi.Dim($"  {InterventionLog.DefaultPath}"));
        if (records.Count == 0)
        {
            w.WriteLine("  No guardrail actions logged yet. Install the hooks (turnroi profile --apply or turnroi hooks install) and work as usual.");
            return 0;
        }

        var from = records.Min(r => r.At).ToLocalTime();
        var to = records.Max(r => r.At).ToLocalTime();
        w.WriteLine($"  {records.Count} actions in {records.Select(r => r.Session).Distinct().Count()} sessions, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
        w.WriteLine();

        var t = new TextTable("Guardrail", "Action", "Times", "Sessions", "Tokens avoided", "Cost avoided").AlignRight(2, 3, 4, 5);
        foreach (var s in summary)
            t.Add(s.Rule, s.Action, s.Count.ToString(), s.Sessions.ToString(),
                s.TokensAvoided > 0 ? $"≥{s.TokensAvoided:N0}" : "-",
                s.CostAvoided is { } c ? $"≥{ConsoleReport.Money(c)}" : "-");
        t.Write(w, "  ");
        w.WriteLine();

        var topFiles = records.Where(r => r.Rule == Rules.RereadGuard && r.Target is not null)
            .GroupBy(r => Display(r.Target!, r.Project)).OrderByDescending(g => g.Sum(r => r.TokensAvoided ?? 0)).Take(5).ToList();
        if (topFiles.Count > 0)
        {
            w.WriteLine(Ansi.Bold("Re-reads blocked most"));
            var ft = new TextTable("File", "Times", "Tokens avoided").AlignRight(1, 2);
            foreach (var g in topFiles) ft.Add(g.Key, g.Count().ToString(), $"≥{g.Sum(r => r.TokensAvoided ?? 0):N0}");
            ft.Write(w, "  ");
            w.WriteLine();
        }

        w.WriteLine(Ansi.Dim("Tokens avoided count each blocked re-read's content once; it would usually have been re-sent on every later call,"));
        w.WriteLine(Ansi.Dim("so real savings are higher. Nudges and budget warnings are counted, not credited: use 'turnroi compare' for their effect."));
        return 0;
    }

    private static string Display(string path, string? project)
    {
        if (project is null) return path;
        var rel = Path.GetRelativePath(project, path);
        return rel.StartsWith("..", StringComparison.Ordinal) ? path : $"{Path.GetFileName(project)}/{rel.Replace('\\', '/')}";
    }
}
