using System.Globalization;
using System.Text;
using TurnRoi.Core.Model;

namespace TurnRoi.Core.Adapters;

/// <summary>
/// Parses the usage export from the Cursor dashboard (one row per usage event, with model, token columns and cost).
///
/// Why the CSV and not Cursor's local database: Cursor keeps chats in a SQLite <c>state.vscdb</c>, but recent builds
/// write zero for the per-message <c>tokenCount</c>, so local data can't attribute spend. The dashboard export is the
/// reliable source of tokens and billed cost.
///
/// Limitation: the export has no prompts, so human turns are approximated by grouping events that occur in a burst
/// (gap of at most <see cref="BurstGap"/>). Treat per-"turn" findings for Cursor as approximate.
/// </summary>
public sealed class CursorUsageCsvAdapter : IAgentSourceAdapter
{
    public static readonly TimeSpan BurstGap = TimeSpan.FromMinutes(2);

    public string Name => "cursor-usage-csv";

    public bool CanRead(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return false;
        using var reader = new StreamReader(path);
        var header = reader.ReadLine() ?? "";
        return header.Contains("Model", StringComparison.OrdinalIgnoreCase)
            && header.Contains("Cache Read", StringComparison.OrdinalIgnoreCase);
    }

    public AgentSession Read(string path) => Parse(File.ReadLines(path), Path.GetFileNameWithoutExtension(path));

    public AgentSession Parse(IEnumerable<string> lines, string sessionId = "cursor-usage")
    {
        var session = new AgentSession { SessionId = sessionId, Source = Name, HasPrompts = false };
        using var e = lines.GetEnumerator();
        if (!e.MoveNext()) return session;

        var header = SplitCsv(e.Current).Select(h => h.Trim().Trim('﻿')).ToArray();
        var cols = Columns.Map(header);
        if (cols.Missing is { } missing)
            throw new FormatException($"Not a recognized Cursor usage export: missing {missing}. Found columns: {string.Join(", ", header)}");

        var events = new List<(DateTimeOffset At, LlmCall Call)>();
        var rowNo = 0;
        var included = 0;
        var supersetRows = 0;

        while (e.MoveNext())
        {
            if (string.IsNullOrWhiteSpace(e.Current)) continue;
            rowNo++;
            var cells = SplitCsv(e.Current);
            string Cell(int idx) => idx >= 0 && idx < cells.Count ? cells[idx].Trim() : "";

            if (!TryParseDate(Cell(cols.Date), out var at)) { session.SkippedLines++; continue; }

            var withoutCw = Num(Cell(cols.InputWithoutCacheWrite));
            var withCw = Num(Cell(cols.InputWithCacheWrite));
            var cacheRead = Num(Cell(cols.CacheRead));
            var output = Num(Cell(cols.Output));
            var total = Num(Cell(cols.Total));

            // Two plausible readings of the input columns; the Total column tells us which one this export uses.
            long input, cacheWrite;
            var disjointTotal = withoutCw + withCw + cacheRead + output;
            var supersetTotal = withCw + cacheRead + output;
            if (cols.InputWithoutCacheWrite >= 0 && cols.InputWithCacheWrite >= 0 && total > 0 && total == supersetTotal && total != disjointTotal)
            {
                input = withoutCw;
                cacheWrite = Math.Max(0, withCw - withoutCw);
                supersetRows++;
            }
            else
            {
                input = cols.InputWithoutCacheWrite >= 0 ? withoutCw : 0;
                cacheWrite = cols.InputWithCacheWrite >= 0 ? withCw : 0;
            }

            decimal? billed = null;
            var costText = Cell(cols.Cost);
            if (TryParseMoney(costText, out var money)) billed = money;
            else if (costText.Length > 0) { billed = 0; included++; }

            var model = Cell(cols.Model);
            events.Add((at, new LlmCall($"row-{rowNo}", model.Length == 0 ? "unknown" : model, at,
                new TokenUsage(input, output, cacheWrite, cacheRead), false, billed)));
        }

        // Exports are newest-first; group oldest-first into bursts. Prompts aren't in the export, so each burst is labelled.
        var bursts = new List<List<LlmCall>>();
        DateTimeOffset last = default;
        foreach (var (at, call) in events.OrderBy(x => x.At))
        {
            if (bursts.Count == 0 || at - last > BurstGap) bursts.Add(new List<LlmCall>());
            bursts[^1].Add(call);
            last = at;
        }

        foreach (var burst in bursts)
        {
            var start = burst[0].Timestamp;
            var turn = new Turn
            {
                Index = session.Turns.Count + 1,
                Prompt = $"{start:yyyy-MM-dd HH:mm} burst of {burst.Count} request{(burst.Count == 1 ? "" : "s")}",
                StartedAt = start,
            };
            turn.Calls.AddRange(burst);
            session.Turns.Add(turn);
        }

        session.Notes.Add($"Cursor's export has no prompts: \"turns\" are bursts of requests less than {BurstGap.TotalMinutes:0} min apart.");
        if (included > 0)
            session.Notes.Add($"{included} event(s) were included in your plan (billed $0); token counts still show their size.");
        if (supersetRows > 0)
            session.Notes.Add($"{supersetRows} row(s) report input including cache writes; cache writes were derived from the difference.");
        return session;
    }

    private sealed record Columns(int Date, int Model, int InputWithoutCacheWrite, int InputWithCacheWrite,
        int CacheRead, int Output, int Total, int Cost)
    {
        public string? Missing =>
            Date < 0 ? "a Date column"
            : Model < 0 ? "a Model column"
            : Output < 0 ? "an Output tokens column"
            : InputWithoutCacheWrite < 0 && InputWithCacheWrite < 0 ? "an Input tokens column"
            : null;

        public static Columns Map(string[] header)
        {
            int Find(Func<string, bool> match)
            {
                for (var i = 0; i < header.Length; i++)
                    if (match(header[i].ToLowerInvariant())) return i;
                return -1;
            }

            return new Columns(
                Date: Find(h => h is "date" or "timestamp" or "time" || h.StartsWith("date")),
                Model: Find(h => h == "model" || h.StartsWith("model")),
                InputWithoutCacheWrite: Find(h => h.Contains("input") && (h.Contains("w/o cache write") || h.Contains("without cache write"))),
                InputWithCacheWrite: Find(h => h.Contains("input") && (h.Contains("w/ cache write") || h.Contains("with cache write"))),
                CacheRead: Find(h => h.Contains("cache read")),
                Output: Find(h => h.Contains("output")),
                Total: Find(h => h.Contains("total")),
                Cost: Find(h => h.Contains("cost")));
        }
    }

    private static bool TryParseDate(string s, out DateTimeOffset value)
    {
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 1_000_000_000_000)
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return true;
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }

    private static long Num(string s)
    {
        if (s.Length == 0) return 0;
        var cleaned = s.Replace(",", "").Replace("_", "");
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (long)d : 0;
    }

    private static bool TryParseMoney(string s, out decimal value)
    {
        var cleaned = s.Replace("$", "").Replace(",", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>RFC 4180 field splitter (quoted fields, doubled quotes). Single-line records only.</summary>
    internal static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
