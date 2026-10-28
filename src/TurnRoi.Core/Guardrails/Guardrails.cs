using System.Text.Json;
using System.Text.Json.Serialization;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Model;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Guardrails;

/// <summary>
/// Settings for the in-session guardrails, read from <c>.turnroi.json</c> in the project folder.
/// Defaults are conservative: only the re-read guard blocks; everything else gives the agent feedback.
/// </summary>
public sealed class GuardrailConfig
{
    /// <summary>"block" (default) or "off". Stops the agent re-reading an unchanged file it already has in context.</summary>
    [JsonPropertyName("rereadGuard")] public string RereadGuard { get; set; } = "block";

    /// <summary>Tool output above this many estimated tokens gets a "filter your output" nudge. 0 turns it off.</summary>
    [JsonPropertyName("outputWarnTokens")] public long OutputWarnTokens { get; set; } = 8_000;

    /// <summary>Per-turn budget in USD (needs a priced model). Null = no USD budget.</summary>
    [JsonPropertyName("turnBudgetUsd")] public decimal? TurnBudgetUsd { get; set; }

    /// <summary>Per-turn budget in total tokens (context + output, summed over the turn's model calls). Null or 0 = off.</summary>
    [JsonPropertyName("turnBudgetTokens")] public long? TurnBudgetTokens { get; set; } = 5_000_000;

    /// <summary>"warn" (default): tell the agent once when the budget is crossed. "block": refuse further tool calls this turn.</summary>
    [JsonPropertyName("budgetAction")] public string BudgetAction { get; set; } = "warn";

    public const string FileName = ".turnroi.json";

    public static GuardrailConfig Load(string? projectDir)
    {
        if (projectDir is null) return new GuardrailConfig();
        var path = Path.Combine(projectDir, FileName);
        if (!File.Exists(path)) return new GuardrailConfig();
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, GuardrailJsonContext.Default.GuardrailConfig) ?? new GuardrailConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new GuardrailConfig(); // A broken config must never break the agent.
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, GuardrailJsonContext.Default.GuardrailConfig);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GuardrailConfig))]
internal sealed partial class GuardrailJsonContext : JsonSerializerContext;

/// <summary>What the hook should do: let the call through, or stop it / give feedback with a message for the agent.</summary>
public sealed record HookDecision(bool Block, string? Feedback)
{
    public static readonly HookDecision Allow = new(false, null);
    public static HookDecision Deny(string reason) => new(true, reason);
    public static HookDecision Tell(string feedback) => new(false, feedback);
}

/// <summary>The parts of a Claude Code hook event the guardrails use.</summary>
public sealed record HookEvent(
    string ToolName,
    string? FilePath,
    string? Range,
    long? ToolResponseChars);

/// <summary>
/// Pure decision logic for Claude Code hooks (the "fast loop"). Callers supply the parsed transcript so this is testable
/// without a running agent. Every rule fails open: when in doubt, allow.
/// </summary>
public sealed class GuardrailEngine(GuardrailConfig config, PriceTable prices, Func<string, DateTimeOffset?> fileModified)
{
    public HookDecision PreToolUse(HookEvent e, AgentSession session)
    {
        if (BudgetExceeded(session) is { } budget && config.BudgetAction.Equals("block", StringComparison.OrdinalIgnoreCase))
            return HookDecision.Deny(
                $"TurnROI turn budget reached ({budget}). Stop calling tools: summarize what is done and what is left, " +
                "and ask the user how to proceed. (Configure in .turnroi.json.)");

        if (e.FilePath is not null && IsReadTool(e.ToolName) && config.RereadGuard.Equals("block", StringComparison.OrdinalIgnoreCase))
        {
            if (FindUnchangedPriorRead(session, e.FilePath, e.Range) is { } priorAt)
            {
                return HookDecision.Deny(
                    $"TurnROI: {e.FilePath} was already read{(e.Range is null ? "" : $" ({e.Range})")} at {priorAt:HH:mm:ss} " +
                    "and has not changed since. Use the content you already have. If you really need it again, " +
                    "read a specific range with offset/limit.");
            }
        }

        return HookDecision.Allow;
    }

    public HookDecision PostToolUse(HookEvent e, AgentSession session, Func<string, bool> firstTimeForKey)
    {
        var messages = new List<string>();

        if (config.OutputWarnTokens > 0 && e.ToolResponseChars is { } chars && chars / 4 >= config.OutputWarnTokens)
        {
            var tokens = chars / 4;
            messages.Add(IsReadTool(e.ToolName)
                ? $"TurnROI: that read returned ~{tokens:N0} tokens. Next time read only the part you need (offset/limit) or search first."
                : $"TurnROI: that output was ~{tokens:N0} tokens and now sits in context for every later call. " +
                  "Next time filter it (e.g. `| tail -n 40`, `| grep -E \"error|fail\"`) or use quiet/summary flags.");
        }

        if (BudgetExceeded(session) is { } budget &&
            config.BudgetAction.Equals("warn", StringComparison.OrdinalIgnoreCase) &&
            firstTimeForKey($"budget-{session.SessionId}-{session.Turns.LastOrDefault()?.Index ?? 0}"))
        {
            messages.Add($"TurnROI: this turn has passed its budget ({budget}). Wrap up: finish the current step, " +
                         "summarize, and check with the user before starting anything new.");
        }

        return messages.Count == 0 ? HookDecision.Allow : HookDecision.Tell(string.Join("\n", messages));
    }

    /// <summary>Describes the exceeded budget, or null when within budget.</summary>
    public string? BudgetExceeded(AgentSession session)
    {
        var turn = session.Turns.LastOrDefault();
        if (turn is null) return null;

        if (config.TurnBudgetUsd is { } usd && usd > 0 && prices.Cost(turn.Calls) is { } cost && cost >= usd)
            return $"${cost:N2} of ${usd:N2}";

        if (config.TurnBudgetTokens is { } max && max > 0 && turn.Usage.Total >= max)
            return $"{turn.Usage.Total:N0} of {max:N0} tokens";

        return null;
    }

    private DateTimeOffset? FindUnchangedPriorRead(AgentSession session, string path, string? range)
    {
        ToolCall? lastRead = null;
        foreach (var tool in session.Turns.SelectMany(t => t.Tools))
        {
            if (tool.Target is null || !SamePath(tool.Target, path)) continue;
            if (ToolKinds.IsEdit(tool)) lastRead = null;
            // Ignore the call being made right now: it has no result yet.
            else if (ToolKinds.IsRead(tool) && tool.ResultChars is > 0 && !tool.IsError && tool.Range == range) lastRead = tool;
        }

        if (lastRead?.Timestamp is not { } readAt) return null;
        // Changed on disk since (by the user, a formatter, a build)? Then reading again is legitimate.
        if (fileModified(path) is not { } modified || modified > readAt) return null;
        return readAt;
    }

    private static bool IsReadTool(string name) => ToolKinds.IsRead(new ToolCall { Id = "", Name = name });

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
