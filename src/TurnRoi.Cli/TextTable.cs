namespace TurnRoi.Cli;

/// <summary>Plain-text table: left-aligned text columns, right-aligned numeric columns.</summary>
internal sealed class TextTable(params string[] headers)
{
    private readonly List<string[]> _rows = new();
    private readonly HashSet<int> _rightAligned = new();

    public TextTable AlignRight(params int[] columns)
    {
        foreach (var c in columns) _rightAligned.Add(c);
        return this;
    }

    public void Add(params string[] cells) => _rows.Add(cells);

    public void Write(TextWriter w, string indent = "")
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, _rows.Count == 0 ? 0 : _rows.Max(r => r[i].Length))).ToArray();
        w.WriteLine(indent + Ansi.Dim(Format(headers, widths)));
        foreach (var row in _rows) w.WriteLine(indent + Format(row, widths));
    }

    private string Format(string[] cells, int[] widths) =>
        string.Join("  ", cells.Select((c, i) => _rightAligned.Contains(i) ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))).TrimEnd();
}

internal static class Ansi
{
    private static readonly bool Enabled =
        !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    private static string Wrap(string code, string s) => Enabled ? $"\u001b[{code}m{s}\u001b[0m" : s;

    public static string Bold(string s) => Wrap("1", s);
    public static string Dim(string s) => Wrap("2", s);
    public static string Red(string s) => Wrap("31", s);
    public static string Yellow(string s) => Wrap("33", s);
    public static string Green(string s) => Wrap("32", s);
    public static string Cyan(string s) => Wrap("36", s);
}
