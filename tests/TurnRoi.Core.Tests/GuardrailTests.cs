using TurnRoi.Core.Guardrails;
using TurnRoi.Core.Model;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class GuardrailTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private const string File = "/repo/src/ClaimValidator.cs";

    private static AgentSession SessionWith(long turnTokens, params ToolCall[] tools)
    {
        var s = new AgentSession { SessionId = "s1", Source = "claude-code" };
        var turn = new Turn { Index = 1, Prompt = "fix" };
        turn.Calls.Add(new LlmCall("m1", "claude-sonnet-4-5", ReadAt, new TokenUsage(turnTokens, 0, 0, 0), false));
        turn.Tools.AddRange(tools);
        s.Turns.Add(turn);
        return s;
    }

    private static ToolCall Read(string path, string? range = null, int? chars = 4_000) =>
        new() { Id = Guid.NewGuid().ToString(), Name = "Read", Target = path, Range = range, ResultChars = chars, Timestamp = ReadAt };

    private static ToolCall Edit(string path) => new() { Id = Guid.NewGuid().ToString(), Name = "Edit", Target = path, Timestamp = ReadAt.AddMinutes(1) };

    private static GuardrailEngine Engine(GuardrailConfig? config = null, DateTimeOffset? modified = null) =>
        new(config ?? new GuardrailConfig(), PriceTable.LoadDefault(), _ => modified ?? ReadAt.AddMinutes(-5));

    private static HookEvent ReadEvent(string? range = null) => new("Read", File, range, null);

    [Fact]
    public void Blocks_rereading_an_unchanged_file()
    {
        var decision = Engine().PreToolUse(ReadEvent(), SessionWith(1_000, Read(File)));

        Assert.True(decision.Block);
        Assert.Contains("already read", decision.Feedback);
    }

    [Fact]
    public void Allows_reread_after_an_edit()
    {
        Assert.False(Engine().PreToolUse(ReadEvent(), SessionWith(1_000, Read(File), Edit(File))).Block);
    }

    [Fact]
    public void Allows_reread_when_the_file_changed_on_disk()
    {
        Assert.False(Engine(modified: ReadAt.AddMinutes(3)).PreToolUse(ReadEvent(), SessionWith(1_000, Read(File))).Block);
    }

    [Fact]
    public void Allows_reading_a_different_range()
    {
        Assert.False(Engine().PreToolUse(ReadEvent("offset=200,limit=100"), SessionWith(1_000, Read(File, "offset=0,limit=200"))).Block);
    }

    [Fact]
    public void Ignores_the_in_flight_read_without_a_result()
    {
        Assert.False(Engine().PreToolUse(ReadEvent(), SessionWith(1_000, Read(File, chars: null))).Block);
    }

    [Fact]
    public void Reread_guard_can_be_turned_off()
    {
        var config = new GuardrailConfig { RereadGuard = "off" };
        Assert.False(Engine(config).PreToolUse(ReadEvent(), SessionWith(1_000, Read(File))).Block);
    }

    [Fact]
    public void Budget_blocks_tools_when_configured_to_block()
    {
        var config = new GuardrailConfig { TurnBudgetTokens = 100_000, BudgetAction = "block" };
        var decision = Engine(config).PreToolUse(new HookEvent("Bash", null, null, null), SessionWith(150_000));

        Assert.True(decision.Block);
        Assert.Contains("budget", decision.Feedback);
    }

    [Fact]
    public void Budget_warns_once_by_default()
    {
        var config = new GuardrailConfig { TurnBudgetTokens = 100_000 };
        var seen = new HashSet<string>();
        var session = SessionWith(150_000);

        var first = Engine(config).PostToolUse(new HookEvent("Bash", null, null, 100), session, seen.Add);
        var second = Engine(config).PostToolUse(new HookEvent("Bash", null, null, 100), session, seen.Add);

        Assert.False(first.Block);
        Assert.Contains("budget", first.Feedback);
        Assert.Null(second.Feedback);
        Assert.False(Engine(config).PreToolUse(new HookEvent("Bash", null, null, null), session).Block);
    }

    [Fact]
    public void Nudges_after_oversized_output()
    {
        var decision = Engine().PostToolUse(new HookEvent("Bash", null, null, 200_000), SessionWith(1_000), _ => true);

        Assert.False(decision.Block);
        Assert.Contains("filter", decision.Feedback);
    }

    [Fact]
    public void Stays_quiet_for_normal_calls()
    {
        Assert.Null(Engine().PostToolUse(new HookEvent("Bash", null, null, 2_000), SessionWith(1_000), _ => true).Feedback);
    }

    [Fact]
    public void Missing_config_file_gives_defaults()
    {
        var config = GuardrailConfig.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Assert.Equal("block", config.RereadGuard);
        Assert.Equal("warn", config.BudgetAction);
    }
}
