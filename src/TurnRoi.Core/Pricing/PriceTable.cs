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

    /// <summary>
    /// Longest-prefix match on a version boundary. Dots and hyphens are treated alike ("claude-sonnet-4.5" matches
    /// "claude-sonnet-4-5"), a provider prefix ("anthropic/", "copilot/") is ignored, and the rest of the id may be a
    /// date ("-20251001") or a variant word ("-thinking", "-codex"), but never another version number: "gpt-5" does
    /// not price "gpt-5.7", so an unknown model stays unpriced instead of borrowing an older model's price.
    /// </summary>
    public ModelPrice? Find(string model)
    {
        var id = model.Trim();
        var slash = id.LastIndexOf('/');
        if (slash >= 0) id = id[(slash + 1)..];
        id = id.Replace('.', '-').ToLowerInvariant();
        // Cursor writes the version first: claude-4.5-sonnet-thinking → claude-sonnet-4-5-thinking.
        id = CursorStyle.Replace(id, "claude-${family}-${version}");

        foreach (var (key, price) in _byPrefix)
        {
            var k = key.Replace('.', '-').ToLowerInvariant();
            if (!id.StartsWith(k, StringComparison.Ordinal)) continue;
            if (IsVariantSuffix(id[k.Length..])) return price;
        }
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex CursorStyle = new(
        @"^claude-(?<version>\d+(?:-\d)?)-(?<family>opus|sonnet|haiku|fable|mythos)(?=$|-)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly HashSet<string> PricedTiers = new(StringComparer.Ordinal)
        { "pro", "mini", "nano", "micro", "lite", "small", "large", "max-pro" };

    private static bool IsVariantSuffix(string rest)
    {
        if (rest.Length == 0) return true;
        if (rest[0] is not ('-' or '@' or ':' or '_')) return false;
        var next = rest[1..];
        if (next.Length == 0) return true;
        if (!char.IsDigit(next[0]))                                   // -thinking, -codex, -latest
            return !PricedTiers.Contains(next.Split('-', '_', ':')[0]);   // but -pro/-mini/-nano are their own prices
        var digits = next.TakeWhile(char.IsDigit).Count();
        return digits >= 6;                                           // a date like 20251001, not a version like 5 or 1
    }

    /// <summary>Cost of a set of calls; null if any call's model is unpriced.</summary>
    public decimal? Cost(IEnumerable<LlmCall> calls)
    {
        decimal total = 0;
        foreach (var c in calls)
        {
            if (c.BilledCost is { } billed) { total += billed; continue; }
            var price = Find(c.Model);
            if (price is null && c.ReportedCost is { } reported) { total += reported; continue; }
            if (price is null) return null;
            total += price.Cost(c.Usage);
        }
        return total;
    }

    public IEnumerable<string> UnpricedModels(IEnumerable<LlmCall> calls) =>
        calls.Where(c => c.BilledCost is null && c.ReportedCost is null && Find(c.Model) is null).Select(c => c.Model).Distinct();

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
