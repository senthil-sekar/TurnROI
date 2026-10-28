using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class CodexRolloutAdapterTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "rollout-2026-10-25T09-00-00-codex-0001.jsonl");

    private static Model.AgentSession Load() => new CodexRolloutAdapter().Read(FixturePath);

    [Fact]
    public void Reads_session_metadata_and_turns()
    {
        var s = Load();

        Assert.Equal("codex-0001", s.SessionId);
        Assert.Equal("/repo/quote-engine", s.Cwd);
        Assert.Equal(new[] { 1, 2 }, s.Turns.Select(t => t.Index).ToArray());
        Assert.Equal("Fix the rounding bug in PremiumCalculator", s.Turns[0].Prompt);
        Assert.Equal(1, s.SkippedLines);
    }

    [Fact]
    public void Turns_cumulative_token_counts_into_per_call_usage_and_ignores_duplicates()
    {
        var calls = Load().Turns[0].Calls;

        Assert.Equal(3, calls.Count);
        Assert.Equal(20_000, calls[0].Usage.Input);
        Assert.Equal(7_000, calls[1].Usage.Input);
        Assert.Equal(18_000, calls[1].Usage.CacheRead);
        Assert.Equal(600, calls[1].Usage.Output);
        Assert.All(calls, c => Assert.Equal("gpt-5-codex", c.Model));
    }

    [Fact]
    public void Extracts_shell_commands_and_patched_files()
    {
        var s = Load();

        Assert.Equal(new[] { "shell", "apply_patch", "shell" }, s.Turns[0].Tools.Select(t => t.Name).ToArray());
        Assert.Equal("cat src/PremiumCalculator.cs", s.Turns[0].Tools[0].Target);
        Assert.Equal("src/PremiumCalculator.cs", s.Turns[0].Tools[1].Target);
        Assert.Equal("dotnet test", s.Turns[0].Tools[2].Target);

        var shellPatch = s.Turns[1].Tools[0];
        Assert.Equal("apply_patch", shellPatch.Name);
        Assert.Equal("tests/PremiumCalculatorTests.cs", shellPatch.Target);
    }

    [Fact]
    public void Reads_exit_codes_from_shell_output()
    {
        var s = Load();

        Assert.False(s.Turns[0].Tools[2].IsError);
        Assert.True(s.Turns[1].Tools[1].IsError);
    }

    [Fact]
    public void Classifies_outcomes_from_patches_and_test_runs()
    {
        var report = new SessionAnalyzer(PriceTable.LoadDefault()).Analyze(Load());

        Assert.Equal(TurnOutcome.Productive, report.Turns[0].Outcome);
        Assert.Equal(TurnOutcome.Failing, report.Turns[1].Outcome);
    }

    [Fact]
    public void Detects_codex_rollouts()
    {
        Assert.Equal(AgentSource.Codex, SessionSources.Detect(FixturePath));
    }
}
