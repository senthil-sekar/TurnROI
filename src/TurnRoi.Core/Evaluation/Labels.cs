using System.Text.Json;
using System.Text.Json.Serialization;
using TurnRoi.Core.Analysis;

namespace TurnRoi.Core.Evaluation;

/// <summary>A person's verdict on one turn, kept to measure how well TurnROI's outcome detection matches people.</summary>
public sealed class TurnLabel
{
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("session")] public string Session { get; set; } = "";
    /// <summary>Where the session was read from, so <c>turnroi eval</c> can re-run the current detector on it.</summary>
    [JsonPropertyName("location")] public string Location { get; set; } = "";
    [JsonPropertyName("turn")] public int Turn { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>What TurnROI said when the label was given.</summary>
    [JsonPropertyName("predicted")] public string Predicted { get; set; } = "";
}

public static class LabelStore
{
    public const string FileName = "labels.jsonl";
    public static string DefaultPath => Path.Combine(Guardrails.InterventionLog.DataDir, FileName);

    public static void Append(TurnLabel label, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.AppendAllText(path, JsonSerializer.Serialize(label, LabelJsonContext.Default.TurnLabel) + "\n");
    }

    /// <summary>All labels; a later label for the same turn replaces an earlier one.</summary>
    public static IReadOnlyList<TurnLabel> Read(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return [];
        var latest = new Dictionary<(string, string, int), TurnLabel>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize(line, LabelJsonContext.Default.TurnLabel) is { } l && Outcome(l.Label) is not null)
                    latest[(l.Source, l.Session, l.Turn)] = l;
            }
            catch (JsonException) { }
        }
        return latest.Values.ToList();
    }

    public static TurnOutcome? Outcome(string text) =>
        text.Trim().ToLowerInvariant() switch
        {
            "p" => TurnOutcome.Productive,
            "u" => TurnOutcome.Unverified,
            "f" => TurnOutcome.Failing,
            "w" => TurnOutcome.Wasted,
            "e" => TurnOutcome.Exploratory,
            var s => Enum.TryParse<TurnOutcome>(s, ignoreCase: true, out var o) && o != TurnOutcome.Unknown ? o : null,
        };
}

public sealed record ClassScore(TurnOutcome Outcome, int Support, int Predicted, int Correct)
{
    public double? Precision => Predicted == 0 ? null : (double)Correct / Predicted;
    public double? Recall => Support == 0 ? null : (double)Correct / Support;
    public double? F1 => Precision is { } p && Recall is { } r && p + r > 0 ? 2 * p * r / (p + r) : null;
}

public sealed record EvalResult(int Total, int Correct, IReadOnlyList<ClassScore> Classes, IReadOnlyDictionary<(TurnOutcome Actual, TurnOutcome Predicted), int> Confusion)
{
    public double Accuracy => Total == 0 ? 0 : (double)Correct / Total;
}

public static class Evaluator
{
    public static readonly TurnOutcome[] Classes =
        [TurnOutcome.Productive, TurnOutcome.Unverified, TurnOutcome.Failing, TurnOutcome.Wasted, TurnOutcome.Exploratory, TurnOutcome.Unknown];

    public static EvalResult Score(IEnumerable<(TurnOutcome Actual, TurnOutcome Predicted)> pairs)
    {
        var list = pairs.ToList();
        var confusion = list.GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
        var classes = Classes
            .Select(c => new ClassScore(c, list.Count(p => p.Actual == c), list.Count(p => p.Predicted == c), list.Count(p => p.Actual == c && p.Predicted == c)))
            .Where(c => c.Support > 0 || c.Predicted > 0)
            .ToList();
        return new EvalResult(list.Count, list.Count(p => p.Actual == p.Predicted), classes, confusion);
    }
}

[JsonSerializable(typeof(TurnLabel))]
internal sealed partial class LabelJsonContext : JsonSerializerContext;
