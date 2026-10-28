namespace TurnRoi.Cli;

/// <summary>Minimal argument parser; keeps the tool dependency-free and AOT-friendly.</summary>
internal sealed class CliArgs
{
    public string Command { get; private set; } = "help";
    public string? Target { get; private set; }
    public string? Root { get; private set; }
    public string? PricesFile { get; private set; }
    public bool Json { get; private set; }
    public bool AllTurns { get; private set; }
    public int Top { get; private set; } = 15;
    public int Limit { get; private set; } = 20;
    public string? Error { get; private set; }

    public const string Help = """
        turnroi — was that agent turn worth its tokens?

        Usage:
          turnroi sessions [--root <dir>] [--limit <n>]
          turnroi analyze [latest | <session-id> | <path.jsonl>] [options]

        Options for analyze:
          --json            Machine-readable report
          --top <n>         Show the n most expensive turns (default 15)
          --all             Show every turn in order
          --prices <file>   Override the built-in price table (JSON)
          --root <dir>      Transcript root (default ~/.claude/projects or $CLAUDE_CONFIG_DIR/projects)

        Other:
          --help, --version

        Flags in the turn table:
          R re-read of an unchanged file    O oversized tool output
          C low prompt-cache hit            X next prompt pushed back
          E repeated tool errors
        """;

    public static CliArgs Parse(string[] args)
    {
        var a = new CliArgs();
        if (args.Length == 0) return a;

        var i = 0;
        var first = args[0];
        if (first is "--help" or "-h" or "help") { a.Command = "help"; return a; }
        if (first is "--version" or "-v") { a.Command = "version"; return a; }
        a.Command = first;
        i++;

        for (; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--json": a.Json = true; break;
                case "--all": a.AllTurns = true; break;
                case "--help" or "-h": a.Command = "help"; return a;
                case "--root": a.Root = Value(ref i); break;
                case "--prices": a.PricesFile = Value(ref i); break;
                case "--top": a.Top = Int(ref i); break;
                case "--limit": a.Limit = Int(ref i); break;
                default:
                    if (arg.StartsWith('-')) { a.Error = $"unknown option '{arg}'"; return a; }
                    if (a.Target is not null) { a.Error = $"unexpected argument '{arg}'"; return a; }
                    a.Target = arg;
                    break;
            }
            if (a.Error is not null) return a;
        }
        return a;

        string? Value(ref int idx)
        {
            if (idx + 1 >= args.Length) { a.Error = $"{args[idx]} needs a value"; return null; }
            return args[++idx];
        }

        int Int(ref int idx)
        {
            var name = args[idx];
            var v = Value(ref idx);
            if (v is null) return 0;
            if (int.TryParse(v, out var n) && n > 0) return n;
            a.Error = $"{name} needs a positive number";
            return 0;
        }
    }
}
