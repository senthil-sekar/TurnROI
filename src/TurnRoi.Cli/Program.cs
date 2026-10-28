using System.Reflection;
using TurnRoi.Cli;
using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Pricing;

return Run(args);

static int Run(string[] args)
{
    var opts = CliArgs.Parse(args);
    if (opts.Error is not null)
    {
        Console.Error.WriteLine($"error: {opts.Error}");
        Console.Error.WriteLine("Run 'turnroi --help' for usage.");
        return 2;
    }

    switch (opts.Command)
    {
        case "help":
            Console.WriteLine(CliArgs.Help);
            return 0;
        case "version":
            Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev");
            return 0;
        case "sessions":
            return ListSessions(opts);
        case "analyze":
            return Analyze(opts);
        case "profile":
            return LoadPrices(opts) is { } p1 ? ProfileCommands.Profile(opts, p1) : 1;
        case "compare":
            return LoadPrices(opts) is { } p2 ? ProfileCommands.Compare(opts, p2) : 1;
        case "hook":
            return Hooks.Run(opts.Target, opts.Agents.Count > 0 ? opts.Agents[0] : null, Console.In, Console.Out, Console.Error);
        case "hooks":
            return Hooks.Manage(opts);
        default:
            Console.Error.WriteLine($"error: unknown command '{opts.Command}'");
            return 2;
    }
}

static int ListSessions(CliArgs opts)
{
    var source = opts.Source ?? AgentSource.Claude;
    if (source == AgentSource.Cursor)
    {
        Console.Error.WriteLine("Cursor has no local session list. Export your usage CSV from the Cursor dashboard,");
        Console.Error.WriteLine("then run: turnroi analyze <usage.csv>");
        return 1;
    }

    var sessions = SessionSources.FindSessions(source, opts.Root, opts.Limit);
    if (sessions.Count == 0)
    {
        var where = opts.Root ?? string.Join(", ", SessionSources.DefaultRoots(source).DefaultIfEmpty("(no default folder found)"));
        Console.Error.WriteLine($"No {source} sessions found under {where}");
        return 1;
    }

    var table = new TextTable("Modified", "Session", "Folder", "Size").AlignRight(3);
    foreach (var f in sessions)
        table.Add(f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), Path.GetFileNameWithoutExtension(f.Name),
            ProjectLabel(source, f), $"{f.Length / 1024.0:N0} KB");
    table.Write(Console.Out);
    return 0;
}

static string ProjectLabel(AgentSource source, FileInfo f) => source switch
{
    // Copilot: <User>/workspaceStorage/<hash>/chatSessions/<id>.jsonl — the hash folder identifies the workspace.
    AgentSource.Copilot => f.Directory?.Name == "emptyWindowChatSessions" ? "(no folder)" : f.Directory?.Parent?.Name ?? "",
    _ => f.Directory?.Name ?? "",
};

static int Analyze(CliArgs opts)
{
    var target = opts.Target ?? "latest";
    var isFile = File.Exists(target);
    var source = opts.Source ?? (isFile ? SessionSources.Detect(target) : AgentSource.Claude);

    if (!isFile && source == AgentSource.Cursor)
    {
        Console.Error.WriteLine("error: Cursor needs a file: export your usage CSV from the Cursor dashboard and pass its path.");
        return 1;
    }

    var file = SessionSources.Resolve(source, target, opts.Root);
    if (file is null)
    {
        Console.Error.WriteLine($"error: no {source} session matching '{target}'. Try 'turnroi sessions --source {source.ToString().ToLowerInvariant()}'.");
        return 1;
    }

    if (LoadPrices(opts) is not { } prices) return 1;

    TurnRoi.Core.Model.AgentSession session;
    try
    {
        session = SessionSources.AdapterFor(source).Read(file.FullName);
    }
    catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: could not read {file.FullName}: {ex.Message}");
        return 1;
    }

    var report = new SessionAnalyzer(prices, git: opts.NoGit ? null : new GitCliHistory()).Analyze(session);

    if (opts.Json)
        JsonReport.Write(Console.Out, report, file.FullName);
    else
        ConsoleReport.Write(Console.Out, report, file.FullName, opts.Top, opts.AllTurns, prices.Note);

    return 0;
}

static PriceTable? LoadPrices(CliArgs opts)
{
    try
    {
        return opts.PricesFile is null ? PriceTable.LoadDefault() : PriceTable.LoadFile(opts.PricesFile);
    }
    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
    {
        Console.Error.WriteLine($"error: could not load prices: {ex.Message}");
        return null;
    }
}
