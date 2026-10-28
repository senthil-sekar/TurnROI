namespace TurnRoi.Core.Model;

/// <summary>Token counts reported by the provider for one or more LLM calls.</summary>
public readonly record struct TokenUsage(long Input, long Output, long CacheWrite, long CacheRead)
{
    public static readonly TokenUsage Zero = new(0, 0, 0, 0);

    /// <summary>Everything the model had to "see" on input, cached or not.</summary>
    public long ContextTokens => Input + CacheWrite + CacheRead;

    public long Total => ContextTokens + Output;

    /// <summary>Share of input context served from prompt cache (0..1). Null when there is no input.</summary>
    public double? CacheHitRatio => ContextTokens == 0 ? null : (double)CacheRead / ContextTokens;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead);

    /// <summary>Field-wise max; used to merge streamed fragments of the same message.</summary>
    public static TokenUsage Max(TokenUsage a, TokenUsage b) =>
        new(Math.Max(a.Input, b.Input), Math.Max(a.Output, b.Output),
            Math.Max(a.CacheWrite, b.CacheWrite), Math.Max(a.CacheRead, b.CacheRead));
}

/// <summary>One request/response round trip to the model.</summary>
public sealed record LlmCall(string MessageId, string Model, DateTimeOffset? Timestamp, TokenUsage Usage, bool IsSidechain);

/// <summary>A tool invocation made by the agent, joined with its result when available.</summary>
public sealed class ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The primary target: file path for file tools, command for shell, pattern for search.</summary>
    public string? Target { get; init; }

    public int? ResultChars { get; set; }
    public bool IsError { get; set; }

    /// <summary>Rough token estimate of the result (chars / 4). Provider-reported usage remains the source of truth.</summary>
    public long? EstimatedResultTokens => ResultChars is null ? null : (long)Math.Ceiling(ResultChars.Value / 4.0);
}

/// <summary>
/// A turn starts with a human prompt and includes every LLM call and tool call the agent made
/// before the next human prompt.
/// </summary>
public sealed class Turn
{
    public required int Index { get; init; }
    public required string Prompt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public List<LlmCall> Calls { get; } = new();
    public List<ToolCall> Tools { get; } = new();

    public TokenUsage Usage => Calls.Aggregate(TokenUsage.Zero, (acc, c) => acc + c.Usage);
    public IEnumerable<string> Models => Calls.Select(c => c.Model).Distinct();
}

public sealed class AgentSession
{
    public required string SessionId { get; init; }
    public required string Source { get; init; }
    public string? Cwd { get; set; }
    public string? GitBranch { get; set; }
    public List<Turn> Turns { get; } = new();

    /// <summary>Lines the adapter could not parse. Surfaced so schema drift is visible, not silent.</summary>
    public int SkippedLines { get; set; }

    public TokenUsage Usage => Turns.Aggregate(TokenUsage.Zero, (acc, t) => acc + t.Usage);
}
