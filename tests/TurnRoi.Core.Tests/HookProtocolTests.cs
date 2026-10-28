using System.Text.Json.Nodes;
using TurnRoi.Core.Apply;
using TurnRoi.Core.Guardrails;

namespace TurnRoi.Core.Tests;

public class HookProtocolTests
{
    private static JsonObject Json(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Fact]
    public void Parses_cursor_payloads_and_detects_cursor()
    {
        var e = HookProtocol.Parse(Json("""
            {"conversation_id":"c1","workspace_roots":["/repo"],"tool_name":"Read","tool_input":{"file_path":"src/A.cs"}}
            """), null);

        Assert.Equal(TargetAgent.Cursor, e.Agent);
        Assert.Equal(Path.GetFullPath("/repo/src/A.cs"), e.FilePath);
        Assert.Equal("c1", e.SessionKey);
        Assert.Equal("/repo", e.ProjectDir);
    }

    [Fact]
    public void Parses_copilot_cli_camel_case_payloads()
    {
        var e = HookProtocol.Parse(Json("""
            {"sessionId":"s9","cwd":"/repo","toolName":"read_file","toolArgs":"{\"filePath\":\"/repo/B.cs\",\"startLine\":1,\"endLine\":20}","toolResult":"abc"}
            """), TargetAgent.Copilot);

        Assert.Equal("read_file", e.ToolName);
        Assert.Equal("/repo/B.cs", e.FilePath);
        Assert.Equal("startLine=1,endLine=20", e.Range);
        Assert.Equal(3, e.ResponseChars);
        Assert.Equal("s9", e.SessionKey);
    }

    [Fact]
    public void Cursor_answers_with_json_permissions()
    {
        var deny = HookProtocol.Format(TargetAgent.Cursor, HookPhase.PreToolUse, HookDecision.Deny("TurnROI: already read. Use it."));
        var allow = HookProtocol.Format(TargetAgent.Cursor, HookPhase.PreToolUse, HookDecision.Allow);
        var post = HookProtocol.Format(TargetAgent.Cursor, HookPhase.PostToolUse, HookDecision.Tell("filter output"));

        Assert.Equal("deny", (string?)JsonNode.Parse(deny.Stdout!)!["permission"]);
        Assert.Equal(0, deny.ExitCode);
        Assert.Equal("allow", (string?)JsonNode.Parse(allow.Stdout!)!["permission"]);
        Assert.Equal("filter output", (string?)JsonNode.Parse(post.Stdout!)!["additional_context"]);
    }

    [Fact]
    public void Claude_and_copilot_answer_on_stderr_with_exit_2()
    {
        var o = HookProtocol.Format(TargetAgent.Copilot, HookPhase.PreToolUse, HookDecision.Deny("stop"));
        Assert.Equal("stop", o.Stderr);
        Assert.Equal(2, o.ExitCode);
        Assert.Equal(0, HookProtocol.Format(TargetAgent.Claude, HookPhase.PostToolUse, HookDecision.Allow).ExitCode);
    }

    [Fact]
    public void Fails_open_with_valid_cursor_output()
    {
        Assert.Equal("{\"permission\":\"allow\"}", HookProtocol.FailOpen(TargetAgent.Cursor, HookPhase.PreToolUse).Stdout);
        Assert.Null(HookProtocol.FailOpen(TargetAgent.Claude, HookPhase.PreToolUse).Stdout);
    }

    [Fact]
    public void Ledger_blocks_rereads_until_the_file_is_edited()
    {
        var dir = Directory.CreateTempSubdirectory("turnroi-ledger-").FullName;
        try
        {
            var readAt = DateTimeOffset.UtcNow;
            var engine = new GuardrailEngine(new GuardrailConfig(), Pricing.PriceTable.LoadDefault(), _ => readAt.AddMinutes(-1));
            var read = new HookInput(TargetAgent.Cursor, "Read", "/repo/A.cs", null, 100, null, "conv", "/repo");
            var edit = read with { ToolName = "Write" };

            var ledger = ReadLedger.Open(dir, TargetAgent.Cursor, "conv");
            Assert.False(engine.PreToolUse(new HookEvent("Read", "/repo/A.cs", null, null), ledger.AsSession("conv")).Block);

            ledger.Record(read, readAt);
            ledger = ReadLedger.Open(dir, TargetAgent.Cursor, "conv"); // reloaded from disk, as the next hook process would
            Assert.True(engine.PreToolUse(new HookEvent("Read", "/repo/A.cs", null, null), ledger.AsSession("conv")).Block);

            ledger.Record(edit, readAt.AddSeconds(5));
            Assert.False(engine.PreToolUse(new HookEvent("Read", "/repo/A.cs", null, null), ledger.AsSession("conv")).Block);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
