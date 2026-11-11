using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurnRoi.Core.Guardrails;

/// <summary>One logged guardrail action, as written by the hooks and read by <c>turnroi savings</c>.</summary>
public sealed class InterventionRecord
{
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    [JsonPropertyName("agent")] public string Agent { get; set; } = "";
    [JsonPropertyName("session")] public string Session { get; set; } = "";
    [JsonPropertyName("project")] public string? Project { get; set; }
    [JsonPropertyName("rule")] public string Rule { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("tokensAvoided")] public long? TokensAvoided { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

/// <summary>
/// Append-only JSON-lines log of guardrail actions in the TurnROI data folder (<c>$TURNROI_HOME</c>, else the user's
/// local application data folder). Writing never throws: a full disk must not break the agent.
/// </summary>
public static class InterventionLog
{
    public const string FileName = "interventions.jsonl";

    public static string DataDir =>
        Environment.GetEnvironmentVariable("TURNROI_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "turnroi");

    public static string DefaultPath => Path.Combine(DataDir, FileName);

    public static void Append(IEnumerable<InterventionRecord> records, string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = records.Select(r => JsonSerializer.Serialize(r, LogJsonContext.Default.InterventionRecord)).ToList();
            if (lines.Count == 0) return;
            File.AppendAllLines(path, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Best effort.
        }
    }

    public static IReadOnlyList<InterventionRecord> Read(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return [];
        var list = new List<InterventionRecord>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize(line, LogJsonContext.Default.InterventionRecord) is { } r) list.Add(r);
            }
            catch (JsonException) { }
        }
        return list;
    }
}

[JsonSerializable(typeof(InterventionRecord))]
internal sealed partial class LogJsonContext : JsonSerializerContext;

/// <summary>Totals for <c>turnroi savings</c>.</summary>
public sealed record RuleSavings(string Rule, string Action, int Count, long TokensAvoided, decimal? CostAvoided, int Sessions);

public static class SavingsReport
{
    /// <summary>
    /// Groups interventions by rule and action. Cost avoided prices the avoided tokens once as uncached input of the
    /// session's model: a floor, since re-read content would usually have been re-sent on every later call.
    /// </summary>
    public static IReadOnlyList<RuleSavings> Summarize(IEnumerable<InterventionRecord> records, Pricing.PriceTable prices) =>
        records
            .GroupBy(r => (r.Rule, r.Action))
            .Select(g =>
            {
                var tokens = g.Sum(r => r.TokensAvoided ?? 0);
                decimal? cost = null;
                if (tokens > 0)
                {
                    decimal sum = 0;
                    var priced = false;
                    foreach (var r in g.Where(r => r.TokensAvoided is > 0))
                    {
                        if (r.Model is null || prices.Find(r.Model) is not { } price) continue;
                        sum += r.TokensAvoided!.Value * price.Input / 1_000_000m;
                        priced = true;
                    }
                    cost = priced ? sum : null;
                }
                return new RuleSavings(g.Key.Rule, g.Key.Action, g.Count(), tokens, cost, g.Select(r => r.Session).Distinct().Count());
            })
            .OrderByDescending(s => s.TokensAvoided).ThenByDescending(s => s.Count)
            .ToList();
}
