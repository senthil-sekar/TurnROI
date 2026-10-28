using TurnRoi.Core.Adapters;

namespace TurnRoi.Core.Tests;

public class ClaudeCodeJsonlAdapterTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-code-basic.jsonl");

    private static Model.AgentSession Load() => new ClaudeCodeJsonlAdapter().Read(FixturePath);

    [Fact]
    public void Reads_session_metadata_and_counts_bad_lines()
    {
        var s = Load();

        Assert.Equal("sess-0001", s.SessionId);
        Assert.Equal("/repo/claims-api", s.Cwd);
        Assert.Equal("main", s.GitBranch);
        Assert.Equal(1, s.SkippedLines);
    }

    [Fact]
    public void Human_prompts_start_turns_but_meta_and_sidechain_prompts_do_not()
    {
        var s = Load();

        Assert.Equal(new[] { 1, 2, 3 }, s.Turns.Select(t => t.Index).ToArray());
        Assert.StartsWith("Add validation", s.Turns[0].Prompt);
        Assert.StartsWith("No, that's wrong", s.Turns[1].Prompt);
        Assert.Equal("Thanks, looks good", s.Turns[2].Prompt);
    }

    [Fact]
    public void Streamed_fragments_of_one_message_count_as_one_call()
    {
        var turn = Load().Turns[0];

        Assert.Equal(5, turn.Calls.Count);
        var first = turn.Calls.Single(c => c.MessageId == "msg_1");
        Assert.Equal(40, first.Usage.Output);
        Assert.Equal(18_000, first.Usage.CacheWrite);
    }

    [Fact]
    public void Sidechain_calls_are_attributed_to_the_parent_turn()
    {
        var turn = Load().Turns[1];

        var sub = Assert.Single(turn.Calls, c => c.IsSidechain);
        Assert.StartsWith("claude-haiku-4-5", sub.Model);
    }

    [Fact]
    public void Synthetic_error_messages_are_not_counted_as_calls()
    {
        Assert.Empty(Load().Turns[2].Calls);
    }

    [Fact]
    public void Tool_results_are_joined_to_their_calls()
    {
        var tools = Load().Turns[0].Tools;

        Assert.Equal(new[] { "Read", "Read", "Edit", "Bash" }, tools.Select(t => t.Name).ToArray());
        var bash = tools.Single(t => t.Name == "Bash");
        Assert.Equal("dotnet test", bash.Target);
        Assert.Equal(90_000, bash.ResultChars);
        Assert.Equal(22_500, bash.EstimatedResultTokens);
    }

    [Fact]
    public void Strips_injected_system_reminders_from_prompts()
    {
        var lines = new[]
        {
            """{"type":"user","message":{"role":"user","content":"<system-reminder>Time is 19:43</system-reminder>\nNo, revert that"}}""",
            """{"type":"user","message":{"role":"user","content":"<system-reminder>only injected context</system-reminder>"}}""",
        };

        var turn = Assert.Single(new ClaudeCodeJsonlAdapter().Parse(lines).Turns, _ => true);

        Assert.Equal("No, revert that", turn.Prompt);
    }

    [Fact]
    public void Tolerates_empty_and_unknown_lines()
    {
        var s = new ClaudeCodeJsonlAdapter().Parse(["", "   ", "{\"type\":\"file-history-snapshot\"}", "[1,2]"], "fallback");

        Assert.Equal("fallback", s.SessionId);
        Assert.Empty(s.Turns);
        Assert.Equal(1, s.SkippedLines);
    }
}
