using System.Text.Json;
using System.Text.Json.Serialization;
using TurnRoi.Core.Analysis;

namespace TurnRoi.Cli;

/// <summary>Stable, versioned JSON shape for scripting and CI. Bump SchemaVersion on breaking changes.</summary>
internal static class JsonReport
{
    public const int SchemaVersion = 1;

    public static void Write(TextWriter w, SessionReport r, string path)
    {
        var s = r.Session;
        var dto = new ReportDto(
            SchemaVersion,
            s.SessionId,
            s.Source,
            path,
            s.Cwd,
            s.GitBranch,
            s.SkippedLines,
            s.Notes,
            Usage(s.Usage),
            r.TotalCost,
            r.CorrectedSpend,
            r.UnpricedModels,
            r.Turns.Select(t => new TurnDto(
                t.Turn.Index,
                t.Turn.Prompt.Length > 200 ? t.Turn.Prompt[..200] : t.Turn.Prompt,
                t.Turn.StartedAt,
                t.Turn.Models.ToList(),
                t.Turn.Calls.Count,
                t.Turn.Tools.Select(x => new ToolDto(x.Name, x.Target, x.EstimatedResultTokens, x.IsError)).ToList(),
                Usage(t.Turn.Usage),
                t.Cost)).ToList(),
            r.Findings.Select(f => new FindingDto(f.Kind.ToString(), f.Severity.ToString(), f.TurnIndex, f.Message, f.EstimatedTokens)).ToList());

        w.WriteLine(JsonSerializer.Serialize(dto, ReportJsonContext.Default.ReportDto));
    }

    private static UsageDto Usage(TurnRoi.Core.Model.TokenUsage u) =>
        new(u.Input, u.Output, u.CacheWrite, u.CacheRead, u.ContextTokens, u.CacheHitRatio);
}

internal sealed record UsageDto(long Input, long Output, long CacheWrite, long CacheRead, long Context, double? CacheHitRatio);
internal sealed record ToolDto(string Name, string? Target, long? EstimatedResultTokens, bool IsError);
internal sealed record TurnDto(int Index, string Prompt, DateTimeOffset? StartedAt, List<string> Models, int LlmCalls,
    List<ToolDto> Tools, UsageDto Usage, decimal? Cost);
internal sealed record FindingDto(string Kind, string Severity, int? Turn, string Message, long? EstimatedTokens);
internal sealed record ReportDto(int SchemaVersion, string SessionId, string Source, string Path, string? Cwd, string? GitBranch,
    int SkippedLines, IReadOnlyList<string> Notes, UsageDto Usage, decimal? TotalCost, decimal? CorrectedSpend, IReadOnlyList<string> UnpricedModels,
    List<TurnDto> Turns, List<FindingDto> Findings);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReportDto))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;
