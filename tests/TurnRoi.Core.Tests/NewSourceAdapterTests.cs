using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class CursorLocalStoreTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static CursorLocalStore Store() => new(Fixture("cursor-user"));

    [Fact]
    public void Lists_chats_with_messages_newest_first_and_maps_folders()
    {
        var chats = Store().List();

        Assert.Equal(new[] { "comp-new", "comp-old" }, chats.Select(c => c.Id).ToArray());
        Assert.Equal("/repo/claims-api", chats[0].Folder);
        Assert.Equal("/repo/claims-api", chats[1].Folder); // from the workspace's own composer list
        Assert.Equal(8, chats[0].Messages);
    }

    [Fact]
    public void Reads_turns_and_tools_from_bubbles()
    {
        var s = Store().Read("comp-new");

        Assert.Equal(2, s.Turns.Count);
        var first = s.Turns[0];
        Assert.Equal("Add validation for negative claim amounts", first.Prompt);
        Assert.Equal(4, first.Tools.Count);
        Assert.Equal("src/ClaimValidator.cs", first.Tools[0].Target);
        Assert.Equal("src/ClaimValidator.cs", first.Tools[2].Target); // target_file spelling
        var test = first.Tools[3];
        Assert.Equal("dotnet test", test.Target);
        Assert.True(test.IsError);
        Assert.True(test.ResultChars > 40_000);
        Assert.Contains("Failed:     2", test.ResultTail);
        Assert.Single(s.Turns[1].Calls);
    }

    [Fact]
    public void Reads_the_legacy_inline_conversation()
    {
        var s = Store().Read("comp-old");

        Assert.Equal("Explain the rating engine", Assert.Single(s.Turns).Prompt);
        Assert.Equal(3000, s.Turns[0].Usage.Input);
    }

    [Fact]
    public void Attributes_usage_export_rows_to_turns_by_time()
    {
        var csv = new CursorUsageCsvAdapter().Read(Fixture("cursor-usage-local.csv")).Turns.SelectMany(t => t.Calls).ToList();
        var s = Store().Read("comp-new", csv);

        Assert.Contains(s.Turns[0].Calls, c => c.Usage.CacheRead == 20_000);
        Assert.Contains(s.Turns[1].Calls, c => c.Usage.CacheRead == 30_000);
        Assert.DoesNotContain(s.Turns.SelectMany(t => t.Calls), c => c.Model == "gpt-5"); // next day: another chat
    }

    [Fact]
    public void The_analyzer_flags_the_reread_the_noisy_test_run_and_the_pushback()
    {
        var report = new SessionAnalyzer(PriceTable.LoadDefault()).Analyze(Store().Read("comp-new"));

        var findings = report.Turns.SelectMany(t => t.Findings).Select(f => f.Kind).ToList();
        Assert.Contains(FindingKind.RepeatedRead, findings);
        Assert.Contains(FindingKind.OversizedToolResult, findings);
        Assert.True(report.Turns[0].WasCorrected);
    }

    [Fact]
    public void Unknown_chat_throws()
    {
        Assert.Throws<KeyNotFoundException>(() => Store().Read("nope"));
    }
}

public class OpenCodeStoreTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Lists_sessions_from_the_database()
    {
        var sessions = new OpenCodeStore(Fixture("opencode-db")).List();

        Assert.Equal(new[] { "ses_A", "ses_B" }, sessions.Select(s => s.Id).ToArray());
        Assert.Equal("Fix rounding", sessions[0].Title);
        Assert.Equal("/repo/quote-engine", sessions[0].Folder);
    }

    [Fact]
    public void Reads_turns_tokens_cost_and_tools()
    {
        var s = new OpenCodeStore(Fixture("opencode-db")).Read("ses_A");

        Assert.Equal(2, s.Turns.Count);
        Assert.Equal("Fix the rounding bug in PremiumCalculator", s.Turns[0].Prompt); // synthetic part skipped
        var call = Assert.Single(s.Turns[0].Calls);
        Assert.Equal(1200, call.Usage.Input);
        Assert.Equal(350, call.Usage.Output); // output + reasoning
        Assert.Equal(2000, call.Usage.CacheWrite);
        Assert.Equal(15000, call.Usage.CacheRead);
        Assert.Equal(0.02m, call.ReportedCost);
        Assert.Equal(new[] { "edit", "bash" }, s.Turns[0].Tools.Select(t => t.Name).ToArray());
        Assert.False(s.Turns[0].Tools[1].IsError);
        Assert.Equal("/repo/quote-engine", s.Cwd);
    }

    [Fact]
    public void Unknown_models_fall_back_to_the_reported_cost()
    {
        var prices = PriceTable.LoadDefault();
        var s = new OpenCodeStore(Fixture("opencode-db")).Read("ses_A");

        Assert.Equal(0.004m, prices.Cost(s.Turns[1].Calls));
        Assert.Empty(prices.UnpricedModels(s.Turns.SelectMany(t => t.Calls)));
    }

    [Fact]
    public void Reads_the_legacy_json_storage()
    {
        var store = new OpenCodeStore(Fixture("opencode-json"));
        var s = store.Read("ses_L");

        Assert.Equal("ses_L", Assert.Single(store.List()).Id);
        var tool = Assert.Single(Assert.Single(s.Turns).Tools);
        Assert.True(tool.IsError);
        Assert.Equal("/repo/legacy/missing.cs", tool.Target);
    }
}

public class CopilotCliAdapterTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "copilot-cli", "abc-123", "events.jsonl");

    [Fact]
    public void Reads_turns_tools_and_spreads_session_totals()
    {
        var adapter = new CopilotCliAdapter();
        Assert.True(adapter.CanRead(FixturePath));
        var s = adapter.Read(FixturePath);

        Assert.Equal("abc-123", s.SessionId);
        Assert.Equal("/repo/claims-api", s.Cwd);
        Assert.Equal(1, s.SkippedLines);
        var turn = Assert.Single(s.Turns);
        Assert.Equal(new[] { "view", "create", "bash" }, turn.Tools.Select(t => t.Name).ToArray());
        Assert.Equal("/repo/claims-api/src/Refund.cs", turn.Tools[0].Target);
        Assert.Contains("Passed:    43", turn.Tools[2].ResultTail);

        Assert.Equal(3, turn.Calls.Count);
        Assert.Equal(500, turn.Calls.Sum(c => c.Usage.Output));
        Assert.Equal(300, turn.Calls[1].Usage.Output);
        Assert.Equal(40000 * 300 / 500, turn.Calls[1].Usage.CacheRead);
        Assert.Equal(3m, turn.Calls.Sum(c => c.ReportedCost ?? 0));
        Assert.Contains(s.Notes, n => n.Contains("spread"));
    }

    [Fact]
    public void Prefers_per_call_usage_events()
    {
        var s = new CopilotCliAdapter().Parse([
            """{"type":"user.message","data":{"content":"hi"},"timestamp":"2026-11-01T09:00:00Z"}""",
            """{"type":"assistant.usage","data":{"model":"gpt-5","inputTokens":1000,"outputTokens":10,"cacheReadTokens":800,"cacheWriteTokens":0}}""",
            """{"type":"assistant.usage","data":{"model":"claude-sonnet-4.5","inputTokens":5,"outputTokens":10,"cacheReadTokens":800,"cacheWriteTokens":0}}""",
            """{"type":"session.shutdown","data":{"modelMetrics":{"gpt-5":{"usage":{"inputTokens":99999}}}}}""",
        ]);

        var calls = Assert.Single(s.Turns).Calls;
        Assert.Equal(200, calls[0].Usage.Input); // cached part of OpenAI-style input subtracted
        Assert.Equal(5, calls[1].Usage.Input);   // Anthropic-style input already excludes the cache
        Assert.DoesNotContain(s.Notes, n => n.Contains("spread"));
    }
}

public class OtelFileAdapterTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Lists_and_reads_claude_code_log_events()
    {
        var adapter = new OtelFileAdapter();
        var path = Fixture("otel-claude-logs.jsonl");
        Assert.True(adapter.CanRead(path));

        Assert.Equal(new[] { "sess-otel-0", "sess-otel-1" }, adapter.List(path).Select(s => s.Id).Order().ToArray());
        var s = adapter.Read(path, "sess-otel-1");

        Assert.Equal(2, s.Turns.Count);
        Assert.Equal("Fix the flaky premium test", s.Turns[0].Prompt);
        var call = Assert.Single(s.Turns[0].Calls);
        Assert.Equal(20000, call.Usage.CacheRead);
        Assert.Equal(4000, call.Usage.CacheWrite);
        Assert.Equal(0.0156m, call.ReportedCost);
        var tool = Assert.Single(s.Turns[0].Tools);
        Assert.Equal("Bash", tool.Name);
        Assert.Equal(60000, tool.ResultChars);
    }

    [Fact]
    public void Reads_genai_spans_with_cached_tokens_out_of_input()
    {
        var adapter = new OtelFileAdapter();
        var path = Fixture("otel-genai-spans.json");
        Assert.True(adapter.CanRead(path));

        var s = adapter.Read(path, "conv-9");

        Assert.Equal(2, s.Turns.Count);
        Assert.Equal("Rename the Quote type", s.Turns[0].Prompt);
        var call = Assert.Single(s.Turns[0].Calls);
        Assert.Equal("gpt-5", call.Model);
        Assert.Equal(10000, call.Usage.Input);
        Assert.Equal(20000, call.Usage.CacheRead);
        Assert.Equal("edit_file", Assert.Single(s.Turns[0].Tools).Name);
        Assert.Equal("src/Quote.cs", s.Turns[0].Tools[0].Target);
        Assert.Equal("gpt-5", Assert.Single(s.Turns[1].Calls).Model);
    }
}

public class SessionSourcesTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("claude-code-basic.jsonl", AgentSource.Claude)]
    [InlineData("copilot-vscode-session.jsonl", AgentSource.Copilot)]
    [InlineData("rollout-2026-10-25T09-00-00-codex-0001.jsonl", AgentSource.Codex)]
    [InlineData("cursor-usage.csv", AgentSource.Cursor)]
    [InlineData("cursor-user/globalStorage/state.vscdb", AgentSource.Cursor)]
    [InlineData("opencode-db/opencode.db", AgentSource.OpenCode)]
    [InlineData("copilot-cli/abc-123/events.jsonl", AgentSource.CopilotCli)]
    [InlineData("otel-claude-logs.jsonl", AgentSource.Otel)]
    [InlineData("otel-genai-spans.json", AgentSource.Otel)]
    public void Detects_the_source_from_the_file(string file, AgentSource expected)
    {
        Assert.Equal(expected, SessionSources.Detect(Fixture(file)));
    }

    [Theory]
    [InlineData("copilot-cli", AgentSource.CopilotCli)]
    [InlineData("CopilotCli", AgentSource.CopilotCli)]
    [InlineData("opencode", AgentSource.OpenCode)]
    [InlineData("OTEL", AgentSource.Otel)]
    public void Parses_source_names(string text, AgentSource expected)
    {
        Assert.Equal(expected, SessionSources.Parse(text));
        Assert.Null(SessionSources.Parse("vim"));
    }

    [Fact]
    public void Lists_and_reads_store_backed_sources()
    {
        var cursor = SessionSources.List(AgentSource.Cursor, Fixture("cursor-user"));
        Assert.Equal("comp-new", cursor[0].Id);
        Assert.Equal(2, SessionSources.Read(cursor[0]).Turns.Count);

        var cli = Assert.Single(SessionSources.List(AgentSource.CopilotCli, Fixture("copilot-cli")));
        Assert.Equal("abc-123", cli.Id);

        var resolved = SessionSources.Resolve(AgentSource.OpenCode, "ses_A", Fixture("opencode-db"));
        Assert.NotNull(resolved);
        Assert.Equal(2, SessionSources.Read(resolved!).Turns.Count);
    }
}
