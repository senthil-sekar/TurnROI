using System.Text.Json.Nodes;
using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Apply;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class ApplyTests
{
    private static RepoGuidance Guidance(string repoDir)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-code-basic.jsonl");
        var report = new SessionAnalyzer(PriceTable.LoadDefault()).Analyze(new ClaudeCodeJsonlAdapter().Read(path));
        var profile = WasteProfile.Build([report]);
        // The fixture's files live under /repo/claims-api; give the outline something to read.
        return RepoGuidance.Build(profile, "/repo/claims-api",
            p => p.EndsWith("ClaimValidator.cs", StringComparison.Ordinal) ? "public class ClaimValidator\n{\n  public bool Validate(Claim c) => true;\n}\n" : null,
            commit: "abc1234");
    }

    [Fact]
    public void Builds_rules_and_skill_from_the_profile()
    {
        var g = Guidance("/repo/claims-api");

        Assert.Contains(g.Rules, r => r.Contains("dotnet test --nologo -v q"));
        Assert.Contains(g.Rules, r => r.Contains("re-read"));
        Assert.Contains(g.Rules, r => r.Contains("plan"));
        var outline = Assert.Single(g.Outlines);
        Assert.Equal("src/ClaimValidator.cs", outline.Path);
        Assert.Contains(outline.Entries, e => e.Line == 1 && e.Text.Contains("class ClaimValidator"));
    }

    [Fact]
    public void Skill_has_valid_quoted_frontmatter_and_commit_stamp()
    {
        var md = Guidance("/repo/claims-api").SkillMarkdown();

        Assert.StartsWith("---\nname: repo-efficiency\ndescription: \"", md.Replace("\r", ""));
        Assert.Contains("commit `abc1234`", md);
        Assert.Contains("`src/ClaimValidator.cs`", md);
    }

    [Theory]
    [InlineData("dotnet test", "dotnet test --nologo -v q")]
    [InlineData("npm install", "npm ci --silent --no-audit --no-fund")]
    [InlineData("pytest", "pytest -q --no-header -rf")]
    [InlineData("cargo test", "cargo -q test")]
    [InlineData("make all", "make all 2>&1 | tail -n 40")]
    public void Suggests_quiet_commands(string command, string expected)
    {
        Assert.Equal(expected, Quiet.For(command));
    }

    [Fact]
    public void Managed_block_is_inserted_then_replaced_in_place()
    {
        var first = ApplyPlanner.UpsertBlock("# Project\n\nUser rules.\n", "- rule one\n");
        var second = ApplyPlanner.UpsertBlock(first + "\nMore user text.\n", "- rule two\n");

        Assert.StartsWith("# Project\n\nUser rules.\n\n" + ApplyPlanner.BlockStart, first);
        Assert.Contains("- rule two", second);
        Assert.DoesNotContain("- rule one", second);
        Assert.EndsWith("More user text.\n", second);
        Assert.Equal("# Project\n\nUser rules.\n\nMore user text.\n", ApplyPlanner.RemoveBlock(second));
    }

    [Fact]
    public void Plans_native_files_for_each_agent()
    {
        var files = new Dictionary<string, string>
        {
            [Path.Combine("/p", "CLAUDE.md")] = "# Mine\n",
            [Path.Combine("/p", ".claude/settings.local.json")] = """{"permissions":{"allow":["Bash(ls:*)"]}}""",
        };
        var (changes, skipped) = ApplyPlanner.Plan("/p", Guidance("/repo/claims-api"),
            new ApplyOptions([TargetAgent.Claude, TargetAgent.Cursor, TargetAgent.Copilot]), p => files.GetValueOrDefault(p));

        var paths = changes.Select(c => c.Path.Replace('\\', '/')).ToList();
        Assert.Empty(skipped);
        Assert.Contains("/p/CLAUDE.md", paths);
        Assert.Contains("/p/.claude/skills/repo-efficiency/SKILL.md", paths);
        Assert.Contains("/p/.claude/settings.local.json", paths);
        Assert.Contains("/p/.cursor/rules/turnroi.mdc", paths);
        Assert.Contains("/p/.cursor/skills/repo-efficiency/SKILL.md", paths);
        Assert.Contains("/p/.cursor/hooks.json", paths);
        Assert.Contains("/p/.github/copilot-instructions.md", paths);
        Assert.Contains("/p/.github/skills/repo-efficiency/SKILL.md", paths);
        Assert.Contains("/p/.github/hooks/turnroi.json", paths);
        Assert.Contains("/p/.turnroi.json", paths);

        var settings = JsonNode.Parse(changes.Single(c => c.Path.EndsWith("settings.local.json", StringComparison.Ordinal)).After!)!;
        Assert.Equal("Bash(ls:*)", (string?)settings["permissions"]!["allow"]![0]);
        Assert.Equal("turnroi hook pre-tool-use", (string?)settings["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]);

        var mdc = changes.Single(c => c.Path.EndsWith(".mdc", StringComparison.Ordinal)).After!;
        Assert.Contains("alwaysApply: true", mdc);
    }

    [Fact]
    public void Never_overwrites_files_turnroi_did_not_create()
    {
        var files = new Dictionary<string, string>
        {
            [Path.Combine("/p", ".claude/skills/repo-efficiency/SKILL.md")] = "---\nname: repo-efficiency\n---\nmy own skill",
        };
        var (changes, skipped) = ApplyPlanner.Plan("/p", Guidance("/repo/claims-api"),
            new ApplyOptions([TargetAgent.Claude], Hooks: false, Config: false), p => files.GetValueOrDefault(p));

        Assert.DoesNotContain(changes, c => c.Path.EndsWith("SKILL.md", StringComparison.Ordinal));
        Assert.Contains(skipped, s => s.Contains("SKILL.md"));
    }

    [Fact]
    public void Hook_merge_is_idempotent_and_removable_for_cursor()
    {
        const string existing = """{"version":1,"hooks":{"stop":[{"command":"./notify.sh"}]}}""";
        var once = HookConfig.MergeCursor(existing, "turnroi");
        var twice = HookConfig.MergeCursor(once, "turnroi");

        Assert.Equal(once, twice);
        Assert.Contains("--agent cursor", once);
        var removed = HookConfig.RemoveCursor(twice)!;
        Assert.Contains("notify.sh", removed);
        Assert.DoesNotContain("turnroi", removed);
    }

    [Fact]
    public void Reinstall_with_another_command_replaces_the_old_entry()
    {
        var a = HookConfig.MergeClaude(null, "dotnet /opt/turnroi.dll");
        var b = HookConfig.MergeClaude(a, "turnroi");

        var pre = JsonNode.Parse(b)!["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Single(pre);
        Assert.Equal("turnroi hook pre-tool-use", (string?)pre[0]!["hooks"]![0]!["command"]);
    }

    [Fact]
    public void Apply_backs_up_and_undo_restores_exactly()
    {
        var dir = Directory.CreateTempSubdirectory("turnroi-apply-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), "# Mine\n");
            var (changes, _) = ApplyPlanner.Plan(dir, Guidance("/repo/claims-api"), new ApplyOptions([TargetAgent.Claude, TargetAgent.Copilot]));

            ApplyRunner.Apply(dir, changes);
            Assert.Contains("turnroi:begin", File.ReadAllText(Path.Combine(dir, "CLAUDE.md")));
            Assert.True(File.Exists(Path.Combine(dir, ".github", "hooks", "turnroi.json")));
            Assert.Equal("*\n", File.ReadAllText(Path.Combine(dir, ".turnroi", ".gitignore")));

            var restored = ApplyRunner.Undo(dir);

            Assert.NotNull(restored);
            Assert.Equal("# Mine\n", File.ReadAllText(Path.Combine(dir, "CLAUDE.md")));
            Assert.False(Directory.Exists(Path.Combine(dir, ".github")));
            Assert.False(Directory.Exists(Path.Combine(dir, ".claude")));
            Assert.Null(ApplyRunner.Undo(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Hook_removal_deletes_the_copilot_file()
    {
        var files = new Dictionary<string, string> { [Path.Combine("/p", ".github/hooks/turnroi.json")] = HookConfig.CopilotFile("turnroi") };
        var change = Assert.Single(ApplyPlanner.PlanHookRemoval("/p", [TargetAgent.Copilot], p => files.GetValueOrDefault(p)));
        Assert.True(change.IsDelete);
    }
}
