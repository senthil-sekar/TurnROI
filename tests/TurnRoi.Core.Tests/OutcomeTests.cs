using TurnRoi.Core.Analysis;
using TurnRoi.Core.Model;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class OutcomeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static Turn TurnWith(int index, string prompt, params ToolCall[] tools)
    {
        var turn = new Turn { Index = index, Prompt = prompt, StartedAt = T0.AddMinutes(index) };
        turn.Calls.Add(new LlmCall($"m{index}", "claude-sonnet-4-5", T0, new TokenUsage(1_000, 100, 0, 0), false));
        turn.Tools.AddRange(tools);
        return turn;
    }

    private static ToolCall Edit(string path) => new() { Id = Guid.NewGuid().ToString(), Name = "Edit", Target = path };

    private static ToolCall Shell(string cmd, string tail, bool error = false) =>
        new() { Id = Guid.NewGuid().ToString(), Name = "Bash", Target = cmd, ResultTail = tail, IsError = error, ResultChars = tail.Length };

    private static SessionReport Analyze(IGitHistory? git, params Turn[] turns)
    {
        var session = new AgentSession { SessionId = "s", Source = "test", Cwd = "/repo" };
        session.Turns.AddRange(turns);
        return new SessionAnalyzer(PriceTable.LoadDefault(), git: git).Analyze(session);
    }

    [Fact]
    public void Edit_with_passing_tests_is_productive()
    {
        var r = Analyze(null, TurnWith(1, "fix it", Edit("/repo/a.cs"), Shell("dotnet test", "Passed!  - Failed:     0, Passed:    12")));
        Assert.Equal(TurnOutcome.Productive, r.Turns[0].Outcome);
    }

    [Fact]
    public void Edit_with_failing_last_test_is_failing()
    {
        var r = Analyze(null, TurnWith(1, "fix it", Edit("/repo/a.cs"), Shell("npm test", "Tests: 2 failed, 10 passed", error: true)));
        Assert.Equal(TurnOutcome.Failing, r.Turns[0].Outcome);
    }

    [Fact]
    public void Edit_without_verification_is_unverified()
    {
        var r = Analyze(null, TurnWith(1, "fix it", Edit("/repo/a.cs")));
        Assert.Equal(TurnOutcome.Unverified, r.Turns[0].Outcome);
    }

    [Fact]
    public void Edit_committed_later_counts_as_productive()
    {
        var git = new FakeGit((T0.AddHours(2), "/repo/a.cs"));
        var r = Analyze(git, TurnWith(1, "fix it", Edit("/repo/a.cs")));

        Assert.Equal(TurnOutcome.Productive, r.Turns[0].Outcome);
        Assert.Equal(1, r.Turns[0].Evidence!.FilesCommittedLater);
    }

    [Fact]
    public void Commit_before_the_turn_does_not_count()
    {
        var git = new FakeGit((T0.AddHours(-1), "/repo/a.cs"));
        var r = Analyze(git, TurnWith(1, "fix it", Edit("/repo/a.cs")));
        Assert.Equal(TurnOutcome.Unverified, r.Turns[0].Outcome);
    }

    [Fact]
    public void Pushback_beats_everything_else()
    {
        var r = Analyze(null,
            TurnWith(1, "fix it", Edit("/repo/a.cs"), Shell("dotnet test", "Passed!")),
            TurnWith(2, "No, that's not what I asked"));
        Assert.Equal(TurnOutcome.Wasted, r.Turns[0].Outcome);
        Assert.Equal(TurnOutcome.Exploratory, r.Turns[1].Outcome);
    }

    [Fact]
    public void Commit_in_turn_is_productive_even_without_edits()
    {
        var r = Analyze(null, TurnWith(1, "commit it", Shell("git commit -m \"fix\"", "[main abc123] fix")));
        Assert.Equal(TurnOutcome.Productive, r.Turns[0].Outcome);
    }

    [Fact]
    public void Summarizes_spend_per_outcome()
    {
        var r = Analyze(null,
            TurnWith(1, "explain", new ToolCall { Id = "r", Name = "Read", Target = "/repo/a.cs" }),
            TurnWith(2, "fix it", Edit("/repo/a.cs")));

        Assert.Equal(new[] { TurnOutcome.Unverified, TurnOutcome.Exploratory }, r.Outcomes.Select(o => o.Outcome).ToArray());
        Assert.All(r.Outcomes, o => Assert.Equal(1, o.Turns));
    }

    [Theory]
    [InlineData("Passed!  - Failed:     0, Passed:    12, Skipped: 0", true)]
    [InlineData("Failed!  - Failed:     2, Passed:    10", false)]
    [InlineData("Tests:       3 passed, 3 total", true)]
    [InlineData("Tests:       1 failed, 2 passed, 3 total", false)]
    [InlineData("===== 5 passed in 0.12s =====", true)]
    [InlineData("===== 1 failed, 4 passed in 0.3s =====", false)]
    [InlineData("ok  \tgithub.com/acme/quote\t0.012s", true)]
    [InlineData("test result: ok. 8 passed; 0 failed", true)]
    [InlineData("Build FAILED.", false)]
    [InlineData("Restoring packages...", null)]
    public void Reads_test_results_from_common_runners(string tail, bool? expected)
    {
        Assert.Equal(expected, ToolKinds.TestPassedResult(new ToolCall { Id = "t", Name = "Bash", Target = "x", ResultTail = tail }));
    }

    [Theory]
    [InlineData("dotnet test --no-build", true)]
    [InlineData("cd api && npm run test", true)]
    [InlineData("python -m pytest -q", true)]
    [InlineData("go test ./...", true)]
    [InlineData("dotnet build", false)]
    [InlineData("cat test/fixtures.json", false)]
    public void Recognizes_test_commands(string command, bool expected)
    {
        Assert.Equal(expected, ToolKinds.IsTestRun(new ToolCall { Id = "t", Name = "Bash", Target = command }));
    }

    [Fact]
    public void Labels_commands_by_their_first_words()
    {
        Assert.Equal("dotnet test", ToolKinds.CommandLabel("cd src && dotnet test --verbosity normal"));
    }

    private sealed class FakeGit(params (DateTimeOffset At, string File)[] commits) : IGitHistory
    {
        public IReadOnlyList<(DateTimeOffset At, IReadOnlySet<string> Files)> CommitsSince(string directory, DateTimeOffset since) =>
            commits.Where(c => c.At >= since)
                .Select(c => (c.At, (IReadOnlySet<string>)new HashSet<string> { Path.GetFullPath(c.File) }))
                .ToList();
    }
}
