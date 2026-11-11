using System.Net;
using System.Text;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Guardrails;

namespace TurnRoi.Cli;

/// <summary>Self-contained HTML reports (no scripts, no external assets) for sharing outside the terminal.</summary>
internal static class HtmlReport
{
    private const string Css = """
        :root { --bg:#fbfbfa; --fg:#1d1d1b; --muted:#6b6a66; --line:#e4e2dd; --card:#ffffff;
                --productive:#2f7d4f; --unverified:#b08900; --failing:#c2492f; --wasted:#a62f5d; --exploratory:#4a6fa5; --unknown:#9a9893; }
        @media (prefers-color-scheme: dark) {
          :root { --bg:#161615; --fg:#ecebe8; --muted:#a3a19c; --line:#2e2d2a; --card:#1e1e1c;
                  --productive:#5cb67f; --unverified:#d9b53f; --failing:#e47a5f; --wasted:#d46a92; --exploratory:#7f9fd1; --unknown:#77756f; }
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--fg); font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width: 1080px; margin: 0 auto; padding: 32px 16px 64px; }
        h1 { font-size: 24px; margin: 0 0 4px; } h2 { font-size: 17px; margin: 32px 0 12px; }
        .sub { color: var(--muted); font-size: 13px; word-break: break-all; }
        .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 12px; margin-top: 20px; }
        .tile { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 12px 14px; }
        .tile .v { font-size: 22px; font-weight: 600; font-variant-numeric: tabular-nums; }
        .tile .k { color: var(--muted); font-size: 12px; }
        .bar { display: flex; height: 14px; border-radius: 7px; overflow: hidden; background: var(--line); }
        .bar span { display: block; height: 100%; }
        .legend { display: flex; flex-wrap: wrap; gap: 6px 16px; margin-top: 8px; font-size: 13px; color: var(--muted); }
        .dot { display: inline-block; width: 10px; height: 10px; border-radius: 3px; margin-right: 6px; vertical-align: -1px; }
        .table-wrap { overflow-x: auto; border: 1px solid var(--line); border-radius: 10px; background: var(--card); }
        table { border-collapse: collapse; width: 100%; font-size: 13px; }
        th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--line); vertical-align: top; }
        th { color: var(--muted); font-weight: 500; white-space: nowrap; } th.n { text-align: right; }
        tr:last-child td { border-bottom: 0; }
        td.n { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
        .pill { display: inline-block; padding: 1px 8px; border-radius: 999px; font-size: 12px; color: #fff; white-space: nowrap; }
        .sev-High { color: var(--failing); font-weight: 600; } .sev-Warning { color: var(--unverified); font-weight: 600; }
        .note { color: var(--muted); font-size: 13px; margin-top: 24px; }
        ol.props li { margin-bottom: 12px; } ol.props .ev { color: var(--muted); }
        code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 12px; }
        """;

    public static string Session(SessionReport r, string path)
    {
        var s = r.Session;
        var u = s.Usage;
        var b = Start($"TurnROI · {s.SessionId}");
        b.Append($"<h1>Session {E(s.SessionId)}</h1><div class=sub>{E(s.Source)} · {E(path)}{(s.Cwd is null ? "" : " · " + E(s.Cwd))}</div>");
        b.Append("<div class=tiles>");
        Tile(b, "Est. cost", ConsoleReport.Money(r.TotalCost));
        Tile(b, "Turns", s.Turns.Count(t => t.Index > 0).ToString());
        Tile(b, "Context tokens", ConsoleReport.Compact(u.ContextTokens));
        Tile(b, "Cache hit", u.CacheHitRatio is { } c ? ConsoleReport.Percent(c) : "-");
        Tile(b, "Findings", r.Findings.Count.ToString());
        b.Append("</div>");

        Outcomes(b, r.Outcomes);

        b.Append("<h2>Turns</h2><div class=table-wrap><table><thead><tr><th>#</th><th>Prompt</th><th>Outcome</th><th>Calls</th><th>Tools</th><th>Context</th><th>Cache</th><th>Cost</th><th>Findings</th></tr></thead><tbody>");
        foreach (var t in r.Turns.Where(t => t.Turn.Calls.Count > 0 || t.Turn.Tools.Count > 0))
        {
            var tu = t.Turn.Usage;
            b.Append($"<tr><td class=n>{t.Turn.Index}</td><td>{E(Clip(t.Turn.Prompt, 140))}</td><td>{Pill(t.Outcome)}</td>" +
                     $"<td class=n>{t.Turn.Calls.Count}</td><td class=n>{t.Turn.Tools.Count}</td><td class=n>{ConsoleReport.Compact(tu.ContextTokens)}</td>" +
                     $"<td class=n>{(tu.CacheHitRatio is { } h ? ConsoleReport.Percent(h) : "-")}</td><td class=n>{ConsoleReport.Money(t.Cost)}</td>" +
                     $"<td>{string.Join("<br>", t.Findings.Select(f => $"<span class=\"sev-{f.Severity}\">{f.Severity}</span> {E(f.Message)}"))}</td></tr>");
        }
        b.Append("</tbody></table></div>");

        if (s.Notes.Count > 0)
            b.Append("<div class=note>").Append(string.Join("<br>", s.Notes.Select(E))).Append("</div>");
        b.Append("<div class=note>Costs are estimates from provider-reported usage; tool-output sizes are chars/4 estimates. Outcomes are heuristics, not verdicts.</div>");
        return End(b);
    }

    public static string Profile(WasteProfile p, string scope)
    {
        var b = Start("TurnROI profile");
        b.Append($"<h1>Waste profile</h1><div class=sub>{E(scope)}</div><div class=tiles>");
        Tile(b, "Sessions", p.Sessions.ToString());
        Tile(b, "Turns", p.Turns.ToString());
        Tile(b, "Est. cost", ConsoleReport.Money(p.Cost));
        Tile(b, "Cost per turn", ConsoleReport.Money(p.CostPerTurn));
        Tile(b, "Cache hit", p.CacheHitRatio is { } c ? ConsoleReport.Percent(c) : "-");
        b.Append("</div>");
        Outcomes(b, p.Outcomes);

        if (p.NoisyCommands.Count > 0)
        {
            b.Append("<h2>Noisiest commands</h2><div class=table-wrap><table><thead><tr><th>Command</th><th>Runs</th><th>Output tokens</th></tr></thead><tbody>");
            foreach (var n in p.NoisyCommands) b.Append($"<tr><td><code>{E(n.Command)}</code></td><td class=n>{n.Runs}</td><td class=n>~{n.Tokens:N0}</td></tr>");
            b.Append("</tbody></table></div>");
        }
        if (p.RepeatedFiles.Count > 0)
        {
            b.Append("<h2>Files re-read without changes</h2><div class=table-wrap><table><thead><tr><th>File</th><th>Re-reads</th><th>Sessions</th><th>Tokens</th></tr></thead><tbody>");
            foreach (var f in p.RepeatedFiles) b.Append($"<tr><td><code>{E(f.File)}</code></td><td class=n>{f.Rereads}</td><td class=n>{f.Sessions}</td><td class=n>~{f.Tokens:N0}</td></tr>");
            b.Append("</tbody></table></div>");
        }

        b.Append($"<h2>Proposed changes ({p.Proposals.Count})</h2>");
        if (p.Proposals.Count == 0) b.Append("<p>Nothing stands out yet.</p>");
        else
        {
            b.Append("<ol class=props>");
            foreach (var pr in p.Proposals) b.Append($"<li><strong>{E(pr.Title)}</strong> <span class=ev>{E(pr.Evidence)}</span><br>{E(pr.Action)}</li>");
            b.Append("</ol>");
        }
        return End(b);
    }

    public static string Savings(IReadOnlyList<RuleSavings> summary, int actions, int sessions)
    {
        var b = Start("TurnROI savings");
        b.Append($"<h1>Guardrail savings</h1><div class=sub>{actions} actions in {sessions} sessions</div>");
        b.Append("<h2>By guardrail</h2><div class=table-wrap><table><thead><tr><th>Guardrail</th><th>Action</th><th>Times</th><th>Sessions</th><th>Tokens avoided</th><th>Cost avoided</th></tr></thead><tbody>");
        foreach (var s in summary)
            b.Append($"<tr><td>{E(s.Rule)}</td><td>{E(s.Action)}</td><td class=n>{s.Count}</td><td class=n>{s.Sessions}</td>" +
                     $"<td class=n>{(s.TokensAvoided > 0 ? $"≥{s.TokensAvoided:N0}" : "-")}</td><td class=n>{(s.CostAvoided is { } c ? "≥" + ConsoleReport.Money(c) : "-")}</td></tr>");
        b.Append("</tbody></table></div><div class=note>Tokens avoided count each blocked re-read once; real savings are higher. Nudges and budget warnings are counted, not credited.</div>");
        return End(b);
    }

    public static string Team(TurnRoi.Core.Team.TeamRow total, IReadOnlyList<TurnRoi.Core.Team.TeamRow> people, IReadOnlyList<TurnRoi.Core.Team.TeamRow> projects)
    {
        var b = Start("TurnROI team");
        b.Append($"<h1>Team rollup</h1><div class=sub>{total.People} people · {total.Sessions} sessions · {total.Turns} turns</div><div class=tiles>");
        Tile(b, "Est. cost", ConsoleReport.Money(total.Cost));
        Tile(b, "Wasted (pushed back)", ConsoleReport.Percent(total.WastedShare));
        Tile(b, "Productive", ConsoleReport.Percent(total.ProductiveShare));
        Tile(b, "Cache hit", total.CacheHit is { } c ? ConsoleReport.Percent(c) : "-");
        Tile(b, "Re-read tokens", ConsoleReport.Compact(total.RereadTokens));
        b.Append("</div>");
        TeamTable(b, "By person", "Person", people);
        TeamTable(b, "By project", "Project", projects);
        b.Append("<div class=note>Built from <code>turnroi export</code> files: numbers only, no prompts, code or paths. Costs are API-equivalent estimates.</div>");
        return End(b);
    }

    private static void TeamTable(StringBuilder b, string title, string key, IReadOnlyList<TurnRoi.Core.Team.TeamRow> rows)
    {
        b.Append($"<h2>{E(title)}</h2><div class=table-wrap><table><thead><tr><th>{E(key)}</th><th class=n>Sessions</th><th class=n>Turns</th><th class=n>Cost</th><th class=n>Wasted</th><th class=n>Productive</th><th class=n>Cache hit</th><th class=n>Re-read tokens</th><th class=n>Oversized output</th></tr></thead><tbody>");
        foreach (var r in rows)
            b.Append($"<tr><td>{E(r.Key)}</td><td class=n>{r.Sessions}</td><td class=n>{r.Turns}</td><td class=n>{ConsoleReport.Money(r.Cost)}</td>" +
                     $"<td class=n>{ConsoleReport.Percent(r.WastedShare)}</td><td class=n>{ConsoleReport.Percent(r.ProductiveShare)}</td>" +
                     $"<td class=n>{(r.CacheHit is { } c ? ConsoleReport.Percent(c) : "-")}</td><td class=n>{ConsoleReport.Compact(r.RereadTokens)}</td><td class=n>{ConsoleReport.Compact(r.OversizedTokens)}</td></tr>");
        b.Append("</tbody></table></div>");
    }

    private static void Outcomes(StringBuilder b, IReadOnlyList<OutcomeTotal> outcomes)
    {
        if (outcomes.Count == 0 || outcomes.All(o => o.Outcome == TurnOutcome.Unknown)) return;
        var priced = outcomes.All(o => o.Cost is not null) && outcomes.Sum(o => o.Cost ?? 0) > 0;
        double total = priced ? (double)outcomes.Sum(o => o.Cost ?? 0) : outcomes.Sum(o => o.Tokens);
        if (total <= 0) return;
        b.Append($"<h2>Where the spend went{(priced ? "" : " <span class=sub>(by tokens)</span>")}</h2><div class=bar role=img aria-label=\"spend by outcome\">");
        foreach (var o in outcomes)
        {
            var share = (priced ? (double)(o.Cost ?? 0) : o.Tokens) / total;
            b.Append($"<span style=\"width:{share * 100:0.##}%;background:var(--{ConsoleReport.OutcomeLabel(o.Outcome)})\" title=\"{ConsoleReport.OutcomeLabel(o.Outcome)} {ConsoleReport.Percent(share)}\"></span>");
        }
        b.Append("</div><div class=legend>");
        foreach (var o in outcomes)
        {
            var share = (priced ? (double)(o.Cost ?? 0) : o.Tokens) / total;
            b.Append($"<span><span class=dot style=\"background:var(--{ConsoleReport.OutcomeLabel(o.Outcome)})\"></span>{ConsoleReport.OutcomeLabel(o.Outcome)} {ConsoleReport.Percent(share)} · {o.Turns} turns{(priced ? " · " + ConsoleReport.Money(o.Cost) : "")}</span>");
        }
        b.Append("</div>");
    }

    private static string Pill(TurnOutcome o) =>
        $"<span class=pill style=\"background:var(--{ConsoleReport.OutcomeLabel(o)})\">{ConsoleReport.OutcomeLabel(o)}</span>";

    private static void Tile(StringBuilder b, string key, string value) =>
        b.Append($"<div class=tile><div class=v>{E(value)}</div><div class=k>{E(key)}</div></div>");

    private static StringBuilder Start(string title) =>
        new StringBuilder("<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content=\"width=device-width, initial-scale=1\">")
            .Append($"<title>{E(title)}</title><style>{Css}</style></head><body><main>");

    private static string End(StringBuilder b) =>
        b.Append($"<div class=note>Generated by TurnROI on {DateTimeOffset.Now:yyyy-MM-dd HH:mm}.</div></main></body></html>").ToString();

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string Clip(string s, int max)
    {
        var one = string.Join(' ', s.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= max ? one : one[..(max - 1)] + "…";
    }
}
