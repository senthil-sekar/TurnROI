using TurnRoi.Core.Apply;
using TurnRoi.Core.Guardrails;
using TurnRoi.Core.Model;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class InterventionTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "turnroi-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void A_blocked_reread_reports_the_tokens_it_saved()
    {
        var at = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var session = new AgentSession { SessionId = "s", Source = "test" };
        var turn = new Turn { Index = 1, Prompt = "x" };
        turn.Tools.Add(new ToolCall { Id = "r", Name = "Read", Target = "/a.cs", ResultChars = 8_000, Timestamp = at });
        session.Turns.Add(turn);

        var d = new GuardrailEngine(new GuardrailConfig(), PriceTable.LoadDefault(), _ => at.AddHours(-1))
            .PreToolUse(new HookEvent("Read", "/a.cs", null, null), session);

        var why = Assert.Single(d.Interventions);
        Assert.Equal(Rules.RereadGuard, why.Rule);
        Assert.Equal(2_000, why.TokensAvoided);
    }

    [Fact]
    public void Log_round_trips_and_skips_bad_lines()
    {
        var path = Path.Combine(TempDir(), "interventions.jsonl");
        InterventionLog.Append([
            new InterventionRecord { At = DateTimeOffset.UtcNow, Agent = "claude", Session = "s1", Rule = Rules.RereadGuard, Action = "blocked", TokensAvoided = 1000, Model = "claude-sonnet-4-5" },
        ], path);
        File.AppendAllText(path, "not json\n");
        InterventionLog.Append([new InterventionRecord { At = DateTimeOffset.UtcNow, Agent = "cursor", Session = "s2", Rule = Rules.OutputNudge, Action = "nudged" }], path);

        var records = InterventionLog.Read(path);
        Assert.Equal(2, records.Count);
        Assert.Equal("cursor", records[1].Agent);
    }

    [Fact]
    public void Savings_are_priced_at_the_input_rate()
    {
        var records = new[]
        {
            new InterventionRecord { Session = "a", Rule = Rules.RereadGuard, Action = "blocked", TokensAvoided = 1_000_000, Model = "claude-sonnet-4-5" },
            new InterventionRecord { Session = "b", Rule = Rules.RereadGuard, Action = "blocked", TokensAvoided = 1_000_000, Model = "claude-sonnet-4-5" },
            new InterventionRecord { Session = "a", Rule = Rules.OutputNudge, Action = "nudged" },
        };

        var summary = SavingsReport.Summarize(records, PriceTable.LoadDefault());

        var reread = Assert.Single(summary, r => r.Rule == Rules.RereadGuard);
        Assert.Equal(2, reread.Count);
        Assert.Equal(2, reread.Sessions);
        Assert.Equal(2_000_000, reread.TokensAvoided);
        Assert.Equal(6m, reread.CostAvoided); // $3 per million input tokens
        Assert.Null(Assert.Single(summary, r => r.Rule == Rules.OutputNudge).CostAvoided);
    }

    [Fact]
    public void Tool_call_budget_counts_ledger_calls_and_resets_each_turn()
    {
        var ledger = ReadLedger.Open(TempDir(), TargetAgent.Cursor, "conv-1");
        var engine = new GuardrailEngine(new GuardrailConfig { TurnBudgetToolCalls = 3, BudgetAction = "block" }, PriceTable.LoadDefault(), _ => null);
        var call = new HookInput(TargetAgent.Cursor, "Shell", null, null, 10, null, "conv-1", null);

        ledger.ObserveTurnKey("gen-1");
        for (var i = 0; i < 3; i++) ledger.Record(call, DateTimeOffset.UtcNow);
        var blocked = engine.PreToolUse(new HookEvent("Shell", null, null, null), ledger.AsSession("conv-1"));
        Assert.True(blocked.Block);
        Assert.Equal(Rules.TurnBudget, Assert.Single(blocked.Interventions).Rule);

        ledger.ObserveTurnKey("gen-1"); // same generation: still over
        Assert.True(engine.PreToolUse(new HookEvent("Shell", null, null, null), ledger.AsSession("conv-1")).Block);

        ledger.ObserveTurnKey("gen-2");
        Assert.False(engine.PreToolUse(new HookEvent("Shell", null, null, null), ledger.AsSession("conv-1")).Block);
    }

    [Fact]
    public void Ledger_keeps_reads_across_turns_and_forgets_edited_files()
    {
        var dir = TempDir();
        var ledger = ReadLedger.Open(dir, TargetAgent.Copilot, "c");
        ledger.Record(new HookInput(TargetAgent.Copilot, "read_file", "/x.cs", null, 400, null, "c", null), DateTimeOffset.UtcNow);
        ledger.NewTurn();

        var reopened = ReadLedger.Open(dir, TargetAgent.Copilot, "c");
        Assert.Equal(1, reopened.Turn);
        Assert.Single(reopened.AsSession("c").Turns[0].Tools);

        reopened.Record(new HookInput(TargetAgent.Copilot, "replace_string_in_file", "/x.cs", null, 10, null, "c", null), DateTimeOffset.UtcNow);
        Assert.Empty(reopened.AsSession("c").Turns[0].Tools);
    }
}

public class SkillFreshnessTests
{
    private const string Source = "namespace A;\npublic class ClaimValidator\n{\n    public bool Validate(int amount) => amount > 0;\n}\n";

    private static (string Dir, Dictionary<string, string> Files) Repo(string fileContent)
    {
        var dir = Path.Combine(Path.GetTempPath(), "turnroi-fresh-" + Guid.NewGuid().ToString("N"));
        var outline = Outline.Of("src/ClaimValidator.cs", Source)!;
        var skill = "# repo-efficiency\n\nLine numbers are from commit `abc1234`.\n\n" + Outline.Section(outline) + "\nmore\n";
        var files = new Dictionary<string, string>
        {
            [Path.Combine(dir, ".claude/skills/repo-efficiency/SKILL.md")] = skill,
            [Path.Combine(dir, "src/ClaimValidator.cs")] = fileContent,
        };
        foreach (var (p, c) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, c);
        }
        return (dir, files);
    }

    [Fact]
    public void Unchanged_file_is_fresh_even_with_crlf()
    {
        var (dir, _) = Repo(Source.Replace("\n", "\r\n"));
        Assert.Equal(OutlineState.Fresh, Assert.Single(SkillFreshness.Check(dir)).State);
        Assert.Empty(SkillFreshness.PlanRefresh(dir, "def5678"));
    }

    [Fact]
    public void Changed_file_is_stale_and_refresh_rewrites_the_outline()
    {
        var changed = "namespace A;\n\n\npublic class ClaimValidator\n{\n    public bool Validate(int amount) => amount > 0;\n    public bool IsRefund(int amount) => amount < 0;\n}\n";
        var (dir, _) = Repo(changed);

        Assert.Equal(OutlineState.Stale, Assert.Single(SkillFreshness.Check(dir)).State);
        var change = Assert.Single(SkillFreshness.PlanRefresh(dir, "def5678"));
        Assert.Contains("IsRefund", change.After);
        Assert.Contains("commit `def5678`", change.After);
        Assert.Contains("more", change.After);

        File.WriteAllText(change.Path, change.After);
        Assert.Equal(OutlineState.Fresh, Assert.Single(SkillFreshness.Check(dir)).State);
    }

    [Fact]
    public void Deleted_file_is_missing_and_refresh_drops_its_outline()
    {
        var (dir, files) = Repo(Source);
        File.Delete(files.Keys.Last());

        Assert.Equal(OutlineState.Missing, Assert.Single(SkillFreshness.Check(dir)).State);
        var change = Assert.Single(SkillFreshness.PlanRefresh(dir, null));
        Assert.DoesNotContain("turnroi:outline", change.After);
    }
}
