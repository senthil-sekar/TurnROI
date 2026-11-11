using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TurnRoi.Core.Analysis;

namespace TurnRoi.Core.Team;

/// <summary>
/// What one person shares with their team: per-session numbers only. No prompts, no code, no file paths, no commands,
/// no session ids (they are hashed). The project is the folder's last name, or a hash of it with <c>hashProjects</c>.
/// </summary>
public sealed class TeamExport
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    [JsonPropertyName("person")] public string Person { get; set; } = "";
    [JsonPropertyName("exportedAt")] public DateTimeOffset ExportedAt { get; set; }
    [JsonPropertyName("sessions")] public List<SessionSummary> Sessions { get; set; } = [];

    public static TeamExport Build(string person, IEnumerable<SessionReport> reports, bool hashProjects, DateTimeOffset now) => new()
    {
        Person = person,
        ExportedAt = now,
        Sessions = reports.Select(r => SessionSummary.From(r, hashProjects)).ToList(),
    };

    public string ToJson() => JsonSerializer.Serialize(this, TeamJsonContext.Default.TeamExport);

    public static TeamExport Parse(string json)
    {
        var e = JsonSerializer.Deserialize(json, TeamJsonContext.Default.TeamExport) ?? throw new InvalidDataException("empty export");
        if (e.Schema > CurrentSchema) throw new InvalidDataException($"export schema {e.Schema} is newer than this TurnROI understands ({CurrentSchema})");
        return e;
    }

    internal static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();
}

public sealed class SessionSummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("project")] public string? Project { get; set; }
    [JsonPropertyName("started")] public DateTimeOffset? Started { get; set; }
    [JsonPropertyName("turns")] public int Turns { get; set; }
    [JsonPropertyName("llmCalls")] public int LlmCalls { get; set; }
    [JsonPropertyName("models")] public List<string> Models { get; set; } = [];
    [JsonPropertyName("input")] public long Input { get; set; }
    [JsonPropertyName("output")] public long Output { get; set; }
    [JsonPropertyName("cacheWrite")] public long CacheWrite { get; set; }
    [JsonPropertyName("cacheRead")] public long CacheRead { get; set; }
    [JsonPropertyName("cost")] public decimal? Cost { get; set; }
    /// <summary>Outcome name → cost of the turns with that outcome.</summary>
    [JsonPropertyName("outcomeCost")] public Dictionary<string, decimal> OutcomeCost { get; set; } = [];
    [JsonPropertyName("outcomeTurns")] public Dictionary<string, int> OutcomeTurns { get; set; } = [];
    /// <summary>Finding kind → estimated tokens involved.</summary>
    [JsonPropertyName("findingTokens")] public Dictionary<string, long> FindingTokens { get; set; } = [];
    [JsonPropertyName("findingCount")] public Dictionary<string, int> FindingCount { get; set; } = [];

    [JsonIgnore] public long Context => Input + CacheWrite + CacheRead;

    public static SessionSummary From(SessionReport r, bool hashProjects)
    {
        var s = r.Session;
        var u = s.Usage;
        var folder = s.Cwd is { Length: > 0 } cwd ? Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd.Replace('\\', '/').TrimEnd('/'))) : null;
        var summary = new SessionSummary
        {
            Id = TeamExport.Hash($"{s.Source}:{s.SessionId}"),
            Source = s.Source,
            Project = folder is null ? null : hashProjects ? TeamExport.Hash(folder) : folder,
            Started = s.Turns.Select(t => t.StartedAt).OfType<DateTimeOffset>().DefaultIfEmpty().Min() is var d && d != default ? d : null,
            Turns = s.Turns.Count(t => t.Index > 0),
            LlmCalls = s.Turns.Sum(t => t.Calls.Count),
            Models = s.Turns.SelectMany(t => t.Models).Distinct().Order(StringComparer.Ordinal).ToList(),
            Input = u.Input, Output = u.Output, CacheWrite = u.CacheWrite, CacheRead = u.CacheRead,
            Cost = r.TotalCost,
        };
        foreach (var o in r.Outcomes)
        {
            summary.OutcomeTurns[o.Outcome.ToString()] = o.Turns;
            if (o.Cost is { } c) summary.OutcomeCost[o.Outcome.ToString()] = c;
        }
        foreach (var g in r.Findings.GroupBy(f => f.Kind.ToString()))
        {
            summary.FindingCount[g.Key] = g.Count();
            summary.FindingTokens[g.Key] = g.Sum(f => f.EstimatedTokens ?? 0);
        }
        return summary;
    }
}

public sealed record TeamRow(
    string Key, int People, int Sessions, int Turns, decimal Cost, decimal WastedCost, decimal ProductiveCost,
    double? CacheHit, long RereadTokens, long OversizedTokens)
{
    public double WastedShare => Cost == 0 ? 0 : (double)(WastedCost / Cost);
    public double ProductiveShare => Cost == 0 ? 0 : (double)(ProductiveCost / Cost);
}

/// <summary>Team totals by person and by project, from everyone's exports.</summary>
public static class TeamRollup
{
    public static IReadOnlyList<TeamRow> By(IEnumerable<TeamExport> exports, Func<TeamExport, SessionSummary, string> key) =>
        exports.SelectMany(e => e.Sessions.Select(s => (Person: e.Person, Session: s, Key: key(e, s))))
            .GroupBy(x => x.Key)
            .Select(g =>
            {
                var sessions = g.Select(x => x.Session).ToList();
                var context = sessions.Sum(s => s.Context);
                return new TeamRow(g.Key, g.Select(x => x.Person).Distinct().Count(), sessions.Count, sessions.Sum(s => s.Turns),
                    sessions.Sum(s => s.Cost ?? 0),
                    sessions.Sum(s => s.OutcomeCost.GetValueOrDefault(nameof(TurnOutcome.Wasted))),
                    sessions.Sum(s => s.OutcomeCost.GetValueOrDefault(nameof(TurnOutcome.Productive))),
                    context == 0 ? null : (double)sessions.Sum(s => s.CacheRead) / context,
                    sessions.Sum(s => s.FindingTokens.GetValueOrDefault(nameof(FindingKind.RepeatedRead))),
                    sessions.Sum(s => s.FindingTokens.GetValueOrDefault(nameof(FindingKind.OversizedToolResult))));
            })
            .OrderByDescending(r => r.Cost)
            .ToList();

    public static IReadOnlyList<TeamRow> ByPerson(IEnumerable<TeamExport> exports) => By(exports, (e, _) => e.Person);
    public static IReadOnlyList<TeamRow> ByProject(IEnumerable<TeamExport> exports) => By(exports, (_, s) => s.Project ?? "(no folder)");
    public static TeamRow Total(IEnumerable<TeamExport> exports) => By(exports, (_, _) => "team").FirstOrDefault()
        ?? new TeamRow("team", 0, 0, 0, 0, 0, 0, null, 0, 0);

    /// <summary>One export per person: when the same person exported twice, the sessions are merged (by hashed id).</summary>
    public static IReadOnlyList<TeamExport> Merge(IEnumerable<TeamExport> exports) =>
        exports.GroupBy(e => e.Person, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TeamExport
            {
                Person = g.First().Person,
                ExportedAt = g.Max(e => e.ExportedAt),
                Sessions = g.OrderByDescending(e => e.ExportedAt).SelectMany(e => e.Sessions).DistinctBy(s => s.Id).ToList(),
            })
            .ToList();
}

[JsonSerializable(typeof(TeamExport))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TeamJsonContext : JsonSerializerContext;
