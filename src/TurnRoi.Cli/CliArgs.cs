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
    public bool NoGit { get; private set; }
    public bool Shared { get; private set; }
    public bool UserScope { get; private set; }
    public string? HookCommand { get; private set; }
    public string? Project { get; private set; }
    public DateTimeOffset? Since { get; private set; }
    public DateTimeOffset? Split { get; private set; }
    public string? WritePath { get; private set; }
    public bool LimitSet { get; private set; }
    public int Top { get; private set; } = 15;
    public int Limit { get; private set; } = 20;
    public string? Error { get; private set; }

    /// <summary>Null means "auto": detect from the file, or Claude Code for "latest"/ids.</summary>
    public Core.Adapters.AgentSource? Source { get; private set; }

    public const string Help = """
        turnroi — was that agent turn worth its tokens?

        Analyze
          turnroi analyze [latest | <session-id> | <file>]   One session: spend per turn, outcomes, waste findings
          turnroi sessions                                   List recent sessions
          turnroi profile                                    Waste patterns across sessions + proposed fixes
          turnroi compare --split <date>                     Before/after a change (e.g. installing the hooks)

        Guardrails (Claude Code)
          turnroi hooks install [--shared | --user]          Add TurnROI hooks to .claude/settings(.local).json
          turnroi hooks uninstall | print
          turnroi hook pre-tool-use | post-tool-use          Called by Claude Code; reads the event on stdin

        Options
          --source <s>      claude (default), copilot, codex or cursor. Files are auto-detected.
          --project <text>  profile/compare: only sessions whose folder contains <text>
          --since <date>    profile/compare: only sessions started on or after <date>
          --limit <n>       How many recent sessions to read (sessions: 20, profile/compare: 100)
          --write <file>    profile: also write the profile and proposals as Markdown
          --json            analyze: machine-readable report
          --top <n> | --all analyze: most expensive n turns (default 15), or every turn in order
          --prices <file>   Override the built-in price table
          --no-git          Don't check git history for edits committed after the session
          --root <dir>      Override where sessions are searched for (hooks: project folder, also --dir)
          --command <cmd>   hooks install: how hooks invoke TurnROI (default "turnroi")

        Turn outcomes
          productive  changed code; tests passed or it was committed      wasted       your next prompt pushed back
          unverified  changed code; no test run or commit seen            exploratory  read/answered, no code changed
          failing     changed code; the last test run failed              unknown      source lacks the data

        Flags in the turn table
          R re-read of an unchanged file   O oversized tool output   C low cache hit   X pushed back   E tool errors
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
                case "--no-git": a.NoGit = true; break;
                case "--shared": a.Shared = true; break;
                case "--user": a.UserScope = true; break;
                case "--command": a.HookCommand = Value(ref i); break;
                case "--dir": a.Root = Value(ref i); break;
                case "--help" or "-h": a.Command = "help"; return a;
                case "--root": a.Root = Value(ref i); break;
                case "--source":
                    var src = Value(ref i);
                    if (src is null) break;
                    if (Enum.TryParse<Core.Adapters.AgentSource>(src, ignoreCase: true, out var parsed)) a.Source = parsed;
                    else a.Error = $"unknown source '{src}' (expected claude, copilot, codex or cursor)";
                    break;
                case "--prices": a.PricesFile = Value(ref i); break;
                case "--top": a.Top = Int(ref i); break;
                case "--limit": a.Limit = Int(ref i); a.LimitSet = true; break;
                case "--project": a.Project = Value(ref i); break;
                case "--write": a.WritePath = Value(ref i); break;
                case "--since": a.Since = Date(ref i); break;
                case "--split": a.Split = Date(ref i); break;
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

        DateTimeOffset? Date(ref int idx)
        {
            var name = args[idx];
            var v = Value(ref idx);
            if (v is null) return null;
            if (DateTimeOffset.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var d)) return d;
            a.Error = $"{name} needs a date like 2026-11-01";
            return null;
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
