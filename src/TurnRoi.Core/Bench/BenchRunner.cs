using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Bench;

/// <summary>
/// A benchmark suite: the same tasks run by the same agent under several variants (e.g. without and with TurnROI's
/// rules, skill and hooks), each in a fresh copy of the repo, checked by a verify command. The question it answers:
/// does a variant cost less <em>at the same success rate</em>?
/// </summary>
public sealed class BenchSuite
{
    /// <summary>Repository to copy for every run (relative to the suite file).</summary>
    [JsonPropertyName("repo")] public string Repo { get; set; } = ".";

    /// <summary>
    /// Command that runs the agent once, non-interactively, in the copy. <c>{prompt}</c> is replaced with the shell-quoted
    /// task prompt; the prompt is also in <c>$TURNROI_PROMPT</c>. E.g. <c>claude -p {prompt} --permission-mode acceptEdits</c>.
    /// </summary>
    [JsonPropertyName("agent")] public string Agent { get; set; } = "";

    /// <summary>Where the agent writes its session (claude, codex, copilot-cli, opencode, ...), to measure the run.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "claude";

    /// <summary>Override where sessions are found (default: the source's usual place).</summary>
    [JsonPropertyName("sessionsRoot")] public string? SessionsRoot { get; set; }

    /// <summary>Default verify command for tasks that have none; exit 0 means the task succeeded.</summary>
    [JsonPropertyName("verify")] public string? Verify { get; set; }

    [JsonPropertyName("timeoutMinutes")] public int TimeoutMinutes { get; set; } = 30;
    [JsonPropertyName("runs")] public int Runs { get; set; } = 1;

    /// <summary>Folder names not copied into each run.</summary>
    [JsonPropertyName("exclude")] public List<string> Exclude { get; set; } = ["bin", "obj", "node_modules", ".vs", ".idea"];

    /// <summary>Variant name → setup commands run in the copy before the agent (e.g. <c>turnroi profile --apply --yes</c>).</summary>
    [JsonPropertyName("variants")] public Dictionary<string, List<string>> Variants { get; set; } = new() { ["baseline"] = [] };

    [JsonPropertyName("tasks")] public List<BenchTask> Tasks { get; set; } = [];

    public static BenchSuite Load(string path)
    {
        var suite = JsonSerializer.Deserialize(File.ReadAllText(path), BenchJsonContext.Default.BenchSuite)
                    ?? throw new InvalidDataException("empty suite");
        if (string.IsNullOrWhiteSpace(suite.Agent)) throw new InvalidDataException("the suite needs an \"agent\" command");
        if (suite.Tasks.Count == 0) throw new InvalidDataException("the suite has no \"tasks\"");
        if (SessionSources.Parse(suite.Source) is null) throw new InvalidDataException($"unknown source '{suite.Source}'");
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        suite.Repo = Path.GetFullPath(Path.Combine(dir, suite.Repo));
        if (suite.SessionsRoot is not null) suite.SessionsRoot = Path.GetFullPath(Path.Combine(dir, suite.SessionsRoot));
        return suite;
    }
}

public sealed class BenchTask
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("verify")] public string? Verify { get; set; }
}

public sealed record BenchRun(
    string Task, string Variant, int Attempt, bool Success, bool TimedOut, int AgentExit, TimeSpan Duration,
    string? SessionId, long? Tokens, decimal? Cost, int Turns, int Findings, string? Note);

public sealed record VariantSummary(
    string Variant, int Runs, int Successes, decimal? TotalCost, long TotalTokens, decimal? CostPerSuccess,
    decimal? CostOnSharedSuccesses, int SharedSuccessTasks);

[JsonSerializable(typeof(BenchSuite))]
[JsonSerializable(typeof(List<BenchRun>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class BenchJsonContext : JsonSerializerContext;

public sealed class BenchRunner(BenchSuite suite, PriceTable prices, Action<string>? log = null)
{
    public IReadOnlyList<BenchRun> Run(IEnumerable<string>? onlyVariants = null)
    {
        var variants = suite.Variants.Where(v => onlyVariants is null || onlyVariants.Contains(v.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        var results = new List<BenchRun>();
        for (var attempt = 1; attempt <= Math.Max(1, suite.Runs); attempt++)
            foreach (var task in suite.Tasks)
                foreach (var (variant, setup) in variants) // interleave variants so drift (model, cache, network) hits both
                {
                    log?.Invoke($"[{task.Id} · {variant} · run {attempt}] ...");
                    var r = RunOne(task, variant, setup, attempt);
                    log?.Invoke($"[{task.Id} · {variant} · run {attempt}] {(r.Success ? "pass" : r.TimedOut ? "timeout" : "fail")}" +
                                $"{(r.Cost is { } c ? $" · ${c:N2}" : "")}{(r.Tokens is { } t ? $" · {t:N0} tokens" : "")}{(r.Note is null ? "" : $" · {r.Note}")}");
                    results.Add(r);
                }
        return results;
    }

    private BenchRun RunOne(BenchTask task, string variant, List<string> setup, int attempt)
    {
        var work = Path.Combine(Path.GetTempPath(), "turnroi-bench", $"{Safe(task.Id)}-{Safe(variant)}-{attempt}-{Guid.NewGuid().ToString("N")[..8]}");
        CopyDirectory(suite.Repo, work, suite.Exclude);
        var env = new Dictionary<string, string> { ["TURNROI_PROMPT"] = task.Prompt, ["TURNROI_TASK"] = task.Id, ["TURNROI_VARIANT"] = variant };
        var timeout = TimeSpan.FromMinutes(Math.Max(1, suite.TimeoutMinutes));

        try
        {
            foreach (var cmd in setup)
            {
                var s = Shell.Run(cmd, work, env, timeout);
                if (s.Exit != 0)
                    return new BenchRun(task.Id, variant, attempt, false, s.TimedOut, s.Exit, s.Duration, null, null, null, 0, 0,
                        $"setup failed: {cmd} (exit {s.Exit}) {Tail(s.Output)}");
            }

            var started = DateTimeOffset.Now;
            var agent = Shell.Run(suite.Agent.Replace("{prompt}", Shell.Quote(task.Prompt)), work, env, timeout);

            var verifyCmd = task.Verify ?? suite.Verify;
            var success = !agent.TimedOut && (verifyCmd is null ? agent.Exit == 0 : Shell.Run(verifyCmd, work, env, timeout).Exit == 0);

            var (sessionId, tokens, cost, turns, findings, note) = Measure(work, started);
            return new BenchRun(task.Id, variant, attempt, success, agent.TimedOut, agent.Exit, agent.Duration,
                sessionId, tokens, cost, turns, findings, note);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private (string? Id, long? Tokens, decimal? Cost, int Turns, int Findings, string? Note) Measure(string work, DateTimeOffset started)
    {
        var source = SessionSources.Parse(suite.Source)!.Value;
        var marker = Path.GetFileName(work);
        foreach (var r in SessionSources.List(source, suite.SessionsRoot, 50))
        {
            if (r.Modified != DateTimeOffset.MinValue && r.Modified < started.AddSeconds(-5)) continue;
            Model.AgentSession s;
            try { s = SessionSources.Read(r); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or FormatException) { continue; }
            // The copy's folder name is unique, so it identifies the run even through symlinked temp paths (macOS /private).
            if (s.Cwd is null || !s.Cwd.Replace('\\', '/').TrimEnd('/').EndsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
            var report = new SessionAnalyzer(prices).Analyze(s);
            return (s.SessionId, s.Usage.Total, report.TotalCost, s.Turns.Count, report.Findings.Count,
                report.UnpricedModels.Count > 0 ? $"unpriced: {string.Join(", ", report.UnpricedModels)}" : null);
        }
        return (null, null, null, 0, 0, "no session found for this run (check \"source\" / \"sessionsRoot\")");
    }

    /// <summary>Per variant: success rate, cost, and cost restricted to tasks every variant solved (the fair comparison).</summary>
    public static IReadOnlyList<VariantSummary> Summarize(IReadOnlyList<BenchRun> runs)
    {
        var variants = runs.Select(r => r.Variant).Distinct().ToList();
        // A (task, attempt) counts as shared when every variant succeeded on it.
        var shared = runs.GroupBy(r => (r.Task, r.Attempt))
            .Where(g => variants.All(v => g.Any(r => r.Variant == v && r.Success)))
            .Select(g => g.Key).ToHashSet();

        return variants.Select(v =>
        {
            var mine = runs.Where(r => r.Variant == v).ToList();
            var ok = mine.Where(r => r.Success).ToList();
            var costed = mine.Where(r => r.Cost is not null).ToList();
            decimal? total = costed.Count == 0 ? null : costed.Sum(r => r.Cost!.Value);
            var okCosted = ok.Where(r => r.Cost is not null).ToList();
            var sharedRuns = mine.Where(r => shared.Contains((r.Task, r.Attempt)) && r.Cost is not null).ToList();
            return new VariantSummary(v, mine.Count, ok.Count, total, mine.Sum(r => r.Tokens ?? 0),
                ok.Count == 0 || total is null ? null : total / ok.Count,
                sharedRuns.Count == 0 ? null : sharedRuns.Sum(r => r.Cost!.Value),
                shared.Select(s => s.Task).Distinct().Count());
        }).ToList();
    }

    public static string ToJson(IReadOnlyList<BenchRun> runs) => JsonSerializer.Serialize(runs.ToList(), BenchJsonContext.Default.ListBenchRun);

    private static void CopyDirectory(string from, string to, IReadOnlyCollection<string> exclude)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(dir);
            if (exclude.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (new DirectoryInfo(dir).LinkTarget is not null) continue;
            CopyDirectory(dir, Path.Combine(to, name), exclude);
        }
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
    private static string Tail(string s) => s.Length <= 300 ? s.Trim() : "…" + s[^300..].Trim();
}

/// <summary>Runs a command line through the platform shell.</summary>
public static class Shell
{
    public sealed record Result(int Exit, bool TimedOut, TimeSpan Duration, string Output);

    public static Result Run(string command, string workingDir, IReadOnlyDictionary<string, string>? env, TimeSpan timeout)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", command } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        psi.WorkingDirectory = workingDir;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;

        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start: {command}");
        p.StandardInput.Close();
        var output = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.WaitForExit();
            return new Result(-1, true, sw.Elapsed, output.ToString());
        }
        p.WaitForExit();
        return new Result(p.ExitCode, false, sw.Elapsed, output.ToString());
    }

    /// <summary>Quotes one argument for the platform shell.</summary>
    public static string Quote(string s) => OperatingSystem.IsWindows()
        ? "\"" + s.Replace("\"", "\\\"") + "\""
        : "'" + s.Replace("'", "'\\''") + "'";
}
