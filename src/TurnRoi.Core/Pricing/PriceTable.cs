using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using TurnRoi.Core.Model;

namespace TurnRoi.Core.Pricing;

/// <summary>USD per million tokens.</summary>
public sealed record ModelPrice(
    [property: JsonPropertyName("input")] decimal Input,
    [property: JsonPropertyName("output")] decimal Output,
    [property: JsonPropertyName("cacheWrite")] decimal CacheWrite,
    [property: JsonPropertyName("cacheRead")] decimal CacheRead)
{
    public decimal Cost(TokenUsage u) =>
        (u.Input * Input + u.Output * Output + u.CacheWrite * CacheWrite + u.CacheRead * CacheRead) / 1_000_000m;
}

/// <summary>
/// Maps model ids to prices by longest matching prefix. Ships with a small embedded table;
/// users can override it with their own JSON file because prices change and new models ship often.
/// Unknown models return null rather than a guess.
/// </summary>
public sealed class PriceTable
{
    private readonly List<KeyValuePair<string, ModelPrice>> _byPrefix;

    public string? Note { get; }

    private PriceTable(Dictionary<string, ModelPrice> prices, string? note)
    {
        _byPrefix = prices.OrderByDescending(p => p.Key.Length).ToList();
        Note = note;
    }

    public ModelPrice? Find(string model) =>
        _byPrefix.FirstOrDefault(p => model.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Cost of a set of calls; null if any call's model is unpriced.</summary>
    public decimal? Cost(IEnumerable<LlmCall> calls)
    {
        decimal total = 0;
        foreach (var c in calls)
        {
            var price = Find(c.Model);
            if (price is null) return null;
            total += price.Cost(c.Usage);
        }
        return total;
    }

    public IEnumerable<string> UnpricedModels(IEnumerable<string> models) => models.Where(m => Find(m) is null).Distinct();

    public static PriceTable LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TurnRoi.Core.Pricing.prices.json")
            ?? throw new InvalidOperationException("Embedded price table missing.");
        return Load(stream);
    }

    public static PriceTable LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    private static PriceTable Load(Stream stream)
    {
        var file = JsonSerializer.Deserialize(stream, PriceJsonContext.Default.PriceFile)
            ?? throw new InvalidOperationException("Price table is empty.");
        return new PriceTable(file.Models ?? new(), file.Note);
    }

}

internal sealed class PriceFile
{
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("models")] public Dictionary<string, ModelPrice>? Models { get; set; }
}

[JsonSerializable(typeof(PriceFile))]
internal sealed partial class PriceJsonContext : JsonSerializerContext;
