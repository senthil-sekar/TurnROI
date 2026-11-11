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
        case "savings":
            return LoadPrices(opts) is { } p3 ? SavingsCommand.Run(opts, p3) : 1;
        case "hook":
            return Hooks.Run(opts.Target, opts.Agents.Count > 0 ? opts.Agents[0] : null, Console.In, Console.Out, Console.Error);
        case "hooks":
            return Hooks.Manage(opts);
        case "bench":
            return LoadPrices(opts) is { } p4 ? BenchCommand.Run(opts, p4) : 1;
        case "export":
            return LoadPrices(opts) is { } p5 ? TeamCommands.Export(opts, p5) : 1;
        case "team":
            return TeamCommands.Team(opts);
        case "label":
            return LoadPrices(opts) is { } p6 ? LabelCommands.Label(opts, p6) : 1;
        case "eval":
            return LoadPrices(opts) is { } p7 ? LabelCommands.Eval(opts, p7) : 1;
        case "mcp":
            return McpServer.Run(Console.In, Console.Out, Run);
        default:
            Console.Error.WriteLine($"error: unknown command '{opts.Command}'");
            return 2;
    }
}

static int ListSessions(CliArgs opts)
{
    var source = opts.Source ?? (opts.Root is { } r && File.Exists(r) ? SessionSources.Detect(r) : AgentSource.Claude);
    IReadOnlyList<SessionRef> sessions;
    try
    {
        sessions = SessionSources.List(source, opts.Root, opts.Limit);
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: could not read {SessionSources.Label(source)} sessions: {ex.Message}");
        return 1;
    }

    if (sessions.Count == 0)
    {
        var where = opts.Root ?? string.Join(", ", SessionSources.DefaultRoots(source).DefaultIfEmpty("(pass the export file with --root)"));
        Console.Error.WriteLine($"No {SessionSources.Label(source)} sessions found under {where}");
        return 1;
    }

    var table = new TextTable("Modified", "Session", "Folder", "Title");
    foreach (var s in sessions)
        table.Add(s.Modified == DateTimeOffset.MinValue ? "-" : s.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), s.Id,
            s.Folder is { } f ? Path.GetFileName(Path.TrimEndingDirectorySeparator(f)) is { Length: > 0 } name ? name : f : "",
            Clip(s.Title ?? "", 50));
    table.Write(Console.Out);
    return 0;
}

static string Clip(string s, int max)
{
    var one = string.Join(' ', s.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
    return one.Length <= max ? one : one[..(max - 1)] + "…";
}

static int Analyze(CliArgs opts)
{
    var target = opts.Target ?? "latest";
    var isFile = File.Exists(target);
    var source = opts.Source ?? (isFile ? SessionSources.Detect(target) : AgentSource.Claude);

    SessionRef? found;
    try
    {
        found = SessionSources.Resolve(source, target, opts.Root);
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: could not read {SessionSources.Label(source)} sessions: {ex.Message}");
        return 1;
    }
    if (found is null)
    {
        Console.Error.WriteLine(source == AgentSource.Otel && !isFile
            ? "error: pass the OpenTelemetry export file (OTLP JSON)."
            : $"error: no {SessionSources.Label(source)} session matching '{target}'. Try 'turnroi sessions --source {SessionSources.Label(source)}'.");
        return 1;
    }

    if (LoadPrices(opts) is not { } prices) return 1;

    TurnRoi.Core.Model.AgentSession session;
    try
    {
        session = SessionSources.Read(found, opts.UsageCsv);
    }
    catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or KeyNotFoundException)
    {
        Console.Error.WriteLine($"error: could not read {found.Location}: {ex.Message}");
        return 1;
    }

    var report = new SessionAnalyzer(prices, git: opts.NoGit ? null : new GitCliHistory()).Analyze(session);
    var label = found.Source is AgentSource.Cursor or AgentSource.OpenCode or AgentSource.Otel ? $"{found.Location} ({found.Id})" : found.Location;

    if (opts.Json)
        JsonReport.Write(Console.Out, report, label);
    else
        ConsoleReport.Write(Console.Out, report, label, opts.Top, opts.AllTurns, prices.Note);

    if (opts.HtmlPath is not null)
    {
        File.WriteAllText(opts.HtmlPath, HtmlReport.Session(report, label));
        Console.Error.WriteLine($"Wrote {opts.HtmlPath}");
    }
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
