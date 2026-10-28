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
        default:
            Console.Error.WriteLine($"error: unknown command '{opts.Command}'");
            return 2;
    }
}

static int ListSessions(CliArgs opts)
{
    var sessions = ClaudeCodeSessionLocator.FindSessions(opts.Root, opts.Limit);
    if (sessions.Count == 0)
    {
        Console.Error.WriteLine($"No Claude Code sessions found under {opts.Root ?? ClaudeCodeSessionLocator.DefaultRoot}");
        return 1;
    }

    var table = new TextTable("Modified", "Session", "Project", "Size");
    foreach (var f in sessions)
        table.Add(f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), Path.GetFileNameWithoutExtension(f.Name),
            f.Directory?.Name ?? "", $"{f.Length / 1024.0:N0} KB");
    table.Write(Console.Out);
    return 0;
}

static int Analyze(CliArgs opts)
{
    var file = ClaudeCodeSessionLocator.Resolve(opts.Target ?? "latest", opts.Root);
    if (file is null)
    {
        Console.Error.WriteLine($"error: no session matching '{opts.Target ?? "latest"}'. Try 'turnroi sessions'.");
        return 1;
    }

    PriceTable prices;
    try
    {
        prices = opts.PricesFile is null ? PriceTable.LoadDefault() : PriceTable.LoadFile(opts.PricesFile);
    }
    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
    {
        Console.Error.WriteLine($"error: could not load prices: {ex.Message}");
        return 1;
    }

    var session = new ClaudeCodeJsonlAdapter().Read(file.FullName);
    var report = new SessionAnalyzer(prices).Analyze(session);

    if (opts.Json)
        JsonReport.Write(Console.Out, report, file.FullName);
    else
        ConsoleReport.Write(Console.Out, report, file.FullName, opts.Top, opts.AllTurns, prices.Note);

    return 0;
}
