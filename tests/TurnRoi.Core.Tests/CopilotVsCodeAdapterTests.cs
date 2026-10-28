using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class CopilotVsCodeAdapterTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static Model.AgentSession LoadLog() => new CopilotVsCodeAdapter().Read(Fixture("copilot-vscode-session.jsonl"));

    [Fact]
    public void Replays_the_mutation_log_into_turns()
    {
        var s = LoadLog();

        Assert.Equal("cop-0001", s.SessionId);
        Assert.Equal(new[] { 1, 2, 3 }, s.Turns.Select(t => t.Index).ToArray());
        Assert.Equal("Add a unit test for PolicyRenewalService", s.Turns[0].Prompt);
        Assert.Equal(1, s.SkippedLines);
    }

    [Fact]
    public void Splits_cached_tokens_out_of_input_from_model_totals()
    {
        var call = Assert.Single(LoadLog().Turns[0].Calls);

        Assert.Equal("claude-sonnet-4.5", call.Model);
        Assert.Equal(30_000, call.Usage.Input);
        Assert.Equal(90_000, call.Usage.CacheRead);
        Assert.Equal(2_500, call.Usage.Output);
    }

    [Fact]
    public void Falls_back_to_last_call_tokens_and_strips_model_prefix()
    {
        var call = Assert.Single(LoadLog().Turns[1].Calls);

        Assert.Equal("gpt-4.1", call.Model);
        Assert.Equal(30_000, call.Usage.Input);
        Assert.Equal(800, call.Usage.Output);
    }

    [Fact]
    public void Records_tool_invocations_with_terminal_commands()
    {
        var tools = LoadLog().Turns[0].Tools;

        Assert.Equal(new[] { "copilot_readFile", "run_in_terminal" }, tools.Select(t => t.Name).ToArray());
        Assert.Equal("dotnet test", tools[1].Target);
    }

    [Fact]
    public void Notes_turns_without_token_data_and_api_equivalent_cost()
    {
        var s = LoadLog();

        Assert.Empty(s.Turns[2].Calls);
        Assert.Contains(s.Notes, n => n.Contains("no token data"));
        Assert.Contains(s.Notes, n => n.Contains("only record the last model call"));
        Assert.Contains(s.Notes, n => n.Contains("API-equivalent"));
    }

    [Fact]
    public void Pushback_detection_works_on_copilot_turns()
    {
        var report = new SessionAnalyzer(PriceTable.LoadDefault()).Analyze(LoadLog());

        Assert.True(report.Turns.Single(t => t.Turn.Index == 1).WasCorrected);
    }

    [Fact]
    public void Reads_legacy_json_snapshots()
    {
        var s = new CopilotVsCodeAdapter().Read(Fixture("copilot-vscode-snapshot.json"));

        var turn = Assert.Single(s.Turns);
        Assert.Equal("Explain this regex", turn.Prompt);
        Assert.Equal(5_000, turn.Usage.Input);
    }

    [Fact]
    public void Push_with_truncate_index_replaces_the_tail()
    {
        var lines = new[]
        {
            """{"kind":0,"v":{"requests":[{"message":{"text":"a"}},{"message":{"text":"b"}}]}}""",
            """{"kind":2,"k":["requests"],"v":[{"message":{"text":"c"}}],"i":1}""",
        };

        var s = new CopilotVsCodeAdapter().ParseLog(lines);

        Assert.Equal(new[] { "a", "c" }, s.Turns.Select(t => t.Prompt).ToArray());
    }

    [Fact]
    public void Detects_copilot_files()
    {
        Assert.Equal(AgentSource.Copilot, SessionSources.Detect(Fixture("copilot-vscode-session.jsonl")));
        Assert.Equal(AgentSource.Copilot, SessionSources.Detect(Fixture("copilot-vscode-snapshot.json")));
        Assert.Equal(AgentSource.Claude, SessionSources.Detect(Fixture("claude-code-basic.jsonl")));
    }
}
