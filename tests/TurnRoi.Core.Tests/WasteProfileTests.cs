using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Model;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class WasteProfileTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static SessionReport Report(AgentSession s) => new SessionAnalyzer(PriceTable.LoadDefault()).Analyze(s);

    [Fact]
    public void Aggregates_sessions_and_proposes_fixes_with_evidence()
    {
        var claude = Report(new ClaudeCodeJsonlAdapter().Read(Fixture("claude-code-basic.jsonl")));
        var codex = Report(new CodexRolloutAdapter().Read(Fixture("rollout-2026-10-25T09-00-00-codex-0001.jsonl")));

        var profile = WasteProfile.Build([claude, codex]);

        Assert.Equal(2, profile.Sessions);
        Assert.Equal(4, profile.Turns);
        Assert.Contains(profile.NoisyCommands, c => c.Command == "dotnet test");
        Assert.Contains(profile.Proposals, p => p.Title.Contains("dotnet test"));
        Assert.Contains(profile.Proposals, p => p.Title.Contains("plan"));
        Assert.Equal("dotnet test", profile.CommonTestCommand);
    }

    [Fact]
    public void Proposes_the_reread_guard_for_frequent_rereads()
    {
        var sessions = Enumerable.Range(1, 3).Select(i =>
        {
            var s = new AgentSession { SessionId = $"s{i}", Source = "test" };
            var turn = new Turn { Index = 1, Prompt = "explain" };
            turn.Calls.Add(new LlmCall("m", "claude-sonnet-4-5", null, new TokenUsage(1_000, 10, 0, 0), false));
            for (var r = 0; r < 3; r++)
                turn.Tools.Add(new ToolCall { Id = $"{i}-{r}", Name = "Read", Target = "/repo/Big.cs", ResultChars = 20_000 });
            s.Turns.Add(turn);
            return Report(s);
        }).ToList();

        var profile = WasteProfile.Build(sessions);

        var file = Assert.Single(profile.RepeatedFiles);
        Assert.Equal(6, file.Rereads);
        Assert.Equal(3, file.Sessions);
        Assert.Contains(profile.Proposals, p => p.Action.Contains("turnroi hooks install"));
        Assert.Contains(profile.Proposals, p => p.Title.Contains("Big.cs"));
    }

    [Fact]
    public void Quiet_history_gets_no_proposals()
    {
        var s = new AgentSession { SessionId = "s", Source = "test" };
        var turn = new Turn { Index = 1, Prompt = "hello" };
        turn.Calls.Add(new LlmCall("m", "claude-sonnet-4-5", null, new TokenUsage(500, 10, 0, 0), false));
        s.Turns.Add(turn);

        Assert.Empty(WasteProfile.Build([Report(s)]).Proposals);
    }

    [Fact]
    public void Markdown_lists_proposals()
    {
        var profile = WasteProfile.Build([Report(new ClaudeCodeJsonlAdapter().Read(Fixture("claude-code-basic.jsonl")))]);
        var md = profile.ToMarkdown("Test");

        Assert.Contains("# Test", md);
        Assert.Contains("## Proposed changes", md);
        Assert.Contains("1. **", md);
    }
}
