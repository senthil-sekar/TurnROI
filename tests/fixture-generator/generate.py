"""Regenerates the synthetic SQLite / JSON fixtures for the newer sources.

Run from the repository root:  python3 tests/fixture-generator/generate.py
All data is invented; never replace these with real agent sessions.
"""
import json
import os
import random
import shutil
import sqlite3
import tempfile

OUT = os.path.join(os.path.dirname(__file__), "..", "TurnRoi.Core.Tests", "Fixtures")
os.chdir(OUT)


def reset(*paths):
    for p in paths:
        if os.path.isdir(p):
            shutil.rmtree(p)
        elif os.path.exists(p):
            os.remove(p)


# ---------------- generic SQLite reader fixtures ----------------
def sqlite_fixtures():
    reset("sqlite-basic.db", "sqlite-wal.db", "sqlite-wal.db-wal", "sqlite-wal.db-shm")
    random.seed(7)
    c = sqlite3.connect("sqlite-basic.db")
    c.execute("PRAGMA page_size=1024")
    c.execute("VACUUM")
    c.execute("CREATE TABLE kv (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)")
    c.execute("CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT, score REAL, n INTEGER, blob BLOB)")
    rows = [(f"bubbleId:c{i % 7}:{i:05d}", ("v%d" % i).encode()) for i in range(1500)]
    rows += [(f"composerData:c{i}", ('{"big":"' + "x" * (5000 * (i + 1)) + '"}').encode()) for i in range(5)]
    random.shuffle(rows)
    c.executemany("INSERT INTO kv VALUES (?,?)", rows)
    ints = [0, 1, -1, 127, -128, 32767, -40000, 8388607, -8388608, 2147483647, -2147483648, 140737488355327, -9007199254740993]
    c.executemany("INSERT INTO items(name,score,n,blob) VALUES (?,?,?,?)",
                  [("é名" * (i % 3) + str(i), i / 3.0, ints[i % 13], bytes([i % 256]) * (i % 5)) for i in range(400)])
    c.commit()
    c.close()

    # WAL: committed rows that haven't been checkpointed into the main file yet.
    tmp = tempfile.mkdtemp()
    db = os.path.join(tmp, "w.db")
    c = sqlite3.connect(db)
    c.execute("PRAGMA page_size=1024")
    c.execute("PRAGMA journal_mode=WAL")
    c.execute("PRAGMA wal_autocheckpoint=0")
    c.execute("CREATE TABLE kv (key TEXT UNIQUE, value BLOB)")
    c.execute("INSERT INTO kv VALUES ('a','1')")
    c.commit()
    c.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    c.executemany("INSERT INTO kv VALUES (?,?)", [(f"w{i:04d}", b"x" * 300) for i in range(200)])
    c.commit()
    c.execute("UPDATE kv SET value='2' WHERE key='a'")
    c.commit()
    shutil.copy(db, "sqlite-wal.db")
    shutil.copy(db + "-wal", "sqlite-wal.db-wal")
    c.close()
    shutil.rmtree(tmp)


# ---------------- Cursor ----------------
def cursor_fixtures():
    reset("cursor-user", "cursor-usage-local.csv")
    u = "cursor-user"
    os.makedirs(u + "/globalStorage")
    os.makedirs(u + "/workspaceStorage/ws1")
    json.dump({"folder": "file:///repo/claims-api"}, open(u + "/workspaceStorage/ws1/workspace.json", "w"))
    w = sqlite3.connect(u + "/workspaceStorage/ws1/state.vscdb")
    w.execute("CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)")
    w.execute("CREATE TABLE cursorDiskKV (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)")
    w.execute("INSERT INTO ItemTable VALUES (?,?)",
              ("composer.composerData", json.dumps({"allComposers": [{"composerId": "comp-old", "name": "old chat"}]})))
    w.commit()
    w.close()

    g = sqlite3.connect(u + "/globalStorage/state.vscdb")
    g.execute("CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)")
    g.execute("CREATE TABLE cursorDiskKV (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)")
    t0 = 1761475200000  # 2025-10-26T10:40:00Z
    g.execute("INSERT INTO ItemTable VALUES (?,?)", ("composer.composerHeaders", json.dumps({"allComposers": [
        {"composerId": "comp-new", "workspaceIdentifier": {"id": "ws1", "uri": {"fsPath": "/repo/claims-api"}}}]})))

    heads = []

    def add(bid, typ, obj):
        o = {"_v": 3, "bubbleId": bid, "type": typ}
        o.update(obj)
        g.execute("INSERT INTO cursorDiskKV VALUES (?,?)", (f"bubbleId:comp-new:{bid}", json.dumps(o)))
        heads.append({"bubbleId": bid, "type": typ})

    def tool(name, call, params, result):
        return {"name": name, "toolCallId": call, "status": "completed", "params": json.dumps(params), "result": json.dumps(result)}

    add("b1", 1, {"text": "Add validation for negative claim amounts", "createdAt": "2025-10-26T10:40:00.000Z"})
    add("b2", 2, {"createdAt": "2025-10-26T10:40:05.000Z", "tokenCount": {"inputTokens": 0, "outputTokens": 0},
                  "toolFormerData": tool("read_file", "t1", {"targetFile": "src/ClaimValidator.cs"}, {"contents": "class A{}" * 500})})
    add("b3", 2, {"createdAt": "2025-10-26T10:40:08.000Z",
                  "toolFormerData": tool("read_file", "t2", {"targetFile": "src/ClaimValidator.cs"}, {"contents": "class A{}" * 500})})
    add("b4", 2, {"createdAt": "2025-10-26T10:40:10.000Z",
                  "toolFormerData": tool("edit_file", "t3", {"target_file": "src/ClaimValidator.cs"}, {"diff": {"chunks": []}})})
    add("b5", 2, {"createdAt": "2025-10-26T10:40:20.000Z",
                  "toolFormerData": tool("run_terminal_cmd", "t4", {"command": "dotnet test"},
                                         {"output": "x" * 40000 + "\nFailed!  - Failed:     2, Passed:    40", "exitCode": 1})})
    add("b6", 2, {"text": "Tests fail because of the refund path.", "createdAt": "2025-10-26T10:40:30.000Z"})
    add("b7", 1, {"text": "No, that's wrong - refunds can be negative. Revert that.", "createdAt": "2025-10-26T10:42:00.000Z"})
    add("b8", 2, {"text": "Reverted.", "createdAt": "2025-10-26T10:42:10.000Z",
                  "tokenCount": {"inputTokens": 1200, "outputTokens": 80}, "modelInfo": {"modelName": "claude-4.5-sonnet-thinking"}})
    g.execute("INSERT INTO cursorDiskKV VALUES (?,?)", ("composerData:comp-new", json.dumps({
        "_v": 10, "composerId": "comp-new", "name": "Negative claim validation", "createdAt": t0, "lastUpdatedAt": t0 + 130000,
        "fullConversationHeadersOnly": heads, "modelConfig": {"modelName": "claude-4.5-sonnet-thinking"}})))
    g.execute("INSERT INTO cursorDiskKV VALUES (?,?)", ("composerData:comp-old", json.dumps({
        "composerId": "comp-old", "name": "old chat", "createdAt": t0 - 86400000, "lastUpdatedAt": t0 - 86000000,
        "conversation": [{"type": 1, "bubbleId": "x1", "text": "Explain the rating engine"},
                         {"type": 2, "bubbleId": "x2", "text": "It works like...", "tokenCount": {"inputTokens": 3000, "outputTokens": 400}}]})))
    g.execute("INSERT INTO cursorDiskKV VALUES (?,?)", ("composerData:comp-empty", json.dumps({"composerId": "comp-empty", "fullConversationHeadersOnly": []})))
    for i in range(3000):  # unrelated keys, so the key index has several levels
        g.execute("INSERT INTO cursorDiskKV VALUES (?,?)", (f"checkpointId:comp-new:{i:05d}", "{}"))
    g.commit()
    g.close()

    open("cursor-usage-local.csv", "w").write(
        "Date,Kind,Model,Max Mode,Input (w/ Cache Write),Input (w/o Cache Write),Cache Read,Output Tokens,Total Tokens,Cost\n"
        "2025-10-26T10:40:06.000Z,Usage-based,claude-4.5-sonnet-thinking,No,0,4000,20000,500,24500,$0.06\n"
        "2025-10-26T10:42:12.000Z,Usage-based,claude-4.5-sonnet-thinking,No,0,1200,30000,80,31280,$0.03\n"
        "2025-10-27T09:00:00.000Z,Usage-based,gpt-5,No,0,100,0,10,110,$0.01\n")


# ---------------- OpenCode ----------------
def opencode_fixtures():
    reset("opencode-db", "opencode-json")
    os.makedirs("opencode-db")
    c = sqlite3.connect("opencode-db/opencode.db")
    c.execute("CREATE TABLE session (id TEXT PRIMARY KEY, project_id TEXT, directory TEXT, title TEXT, time_created INTEGER, time_updated INTEGER)")
    c.execute("CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, time_created INTEGER, data TEXT NOT NULL)")
    c.execute("CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT NOT NULL, time_created INTEGER, data TEXT NOT NULL)")
    s0 = 1762000000000
    c.execute("INSERT INTO session VALUES (?,?,?,?,?,?)", ("ses_A", "prj", "/repo/quote-engine", "Fix rounding", s0, s0 + 60000))
    c.execute("INSERT INTO session VALUES (?,?,?,?,?,?)", ("ses_B", "prj", "/repo/quote-engine", "Older", s0 - 999999, s0 - 900000))

    def msg(mid, t, data):
        c.execute("INSERT INTO message VALUES (?,?,?,?)", (mid, "ses_A", t, json.dumps(data)))

    def part(pid, mid, data):
        c.execute("INSERT INTO part VALUES (?,?,?,?,?)", (pid, mid, "ses_A", s0, json.dumps(data)))

    msg("msg_01", s0, {"id": "msg_01", "role": "user", "time": {"created": s0}})
    part("prt_01", "msg_01", {"type": "text", "text": "Fix the rounding bug in PremiumCalculator"})
    part("prt_02", "msg_01", {"type": "text", "text": "<context>", "synthetic": True})
    msg("msg_02", s0 + 1000, {"id": "msg_02", "role": "assistant", "modelID": "claude-sonnet-4-5", "providerID": "anthropic",
                              "tokens": {"input": 1200, "output": 300, "reasoning": 50, "cache": {"read": 15000, "write": 2000}},
                              "cost": 0.02, "time": {"created": s0 + 1000}, "path": {"cwd": "/repo/quote-engine"}})
    part("prt_03", "msg_02", {"type": "tool", "tool": "edit", "callID": "call_1",
                              "state": {"status": "completed", "input": {"filePath": "/repo/quote-engine/src/PremiumCalculator.cs"}, "output": "ok"}})
    part("prt_04", "msg_02", {"type": "tool", "tool": "bash", "callID": "call_2",
                              "state": {"status": "completed", "input": {"command": "dotnet test"},
                                        "output": "Passed!  - Failed:     0, Passed:    42", "metadata": {"exit": 0}}})
    msg("msg_03", s0 + 30000, {"id": "msg_03", "role": "user", "time": {"created": s0 + 30000}})
    part("prt_05", "msg_03", {"type": "text", "text": "Thanks"})
    msg("msg_04", s0 + 31000, {"id": "msg_04", "role": "assistant", "modelID": "some-new-model",
                               "tokens": {"input": 100, "output": 20, "cache": {"read": 0, "write": 0}}, "cost": 0.004, "time": {"created": s0 + 31000}})
    c.commit()
    c.close()

    base = "opencode-json/storage"
    for d in ["session/prj", "message/ses_L", "part/msg_L1", "part/msg_L2"]:
        os.makedirs(f"{base}/{d}")
    json.dump({"id": "ses_L", "title": "Legacy", "directory": "/repo/legacy", "time": {"created": s0, "updated": s0 + 5000}},
              open(f"{base}/session/prj/ses_L.json", "w"))
    json.dump({"id": "msg_L1", "sessionID": "ses_L", "role": "user", "time": {"created": s0}}, open(f"{base}/message/ses_L/msg_L1.json", "w"))
    json.dump({"id": "prt_L1", "messageID": "msg_L1", "sessionID": "ses_L", "type": "text", "text": "List the files"},
              open(f"{base}/part/msg_L1/prt_L1.json", "w"))
    json.dump({"id": "msg_L2", "sessionID": "ses_L", "role": "assistant", "modelID": "gpt-5",
               "tokens": {"input": 500, "output": 50, "reasoning": 0, "cache": {"read": 0, "write": 0}}, "time": {"created": s0 + 1000}},
              open(f"{base}/message/ses_L/msg_L2.json", "w"))
    json.dump({"id": "prt_L2", "messageID": "msg_L2", "sessionID": "ses_L", "type": "tool", "tool": "read", "callID": "c9",
               "state": {"status": "error", "input": {"filePath": "/repo/legacy/missing.cs"}, "error": "not found"}},
              open(f"{base}/part/msg_L2/prt_L2.json", "w"))


# ---------------- Copilot CLI ----------------
def copilot_cli_fixture():
    reset("copilot-cli")
    os.makedirs("copilot-cli/abc-123")
    lines = []

    def ev(t, ts, data):
        lines.append(json.dumps({"type": t, "data": data, "id": f"e{len(lines)}", "timestamp": f"2026-11-01T09:00:{ts:02d}.000Z", "parentId": None}))

    ev("session.start", 0, {"sessionId": "abc-123", "copilotVersion": "1.0", "context": {"cwd": "/repo/claims-api"}})
    ev("session.model_change", 1, {"newModel": "claude-sonnet-4.5"})
    ev("user.message", 2, {"content": "Add a regression test for refunds"})
    ev("assistant.message", 3, {"content": "Looking", "outputTokens": 100,
                                "toolRequests": [{"toolCallId": "tc1", "name": "view", "arguments": {"path": "/repo/claims-api/src/Refund.cs"}}]})
    ev("tool.execution_start", 4, {"toolCallId": "tc1", "toolName": "view", "arguments": {"path": "/repo/claims-api/src/Refund.cs"}})
    ev("tool.execution_complete", 5, {"toolCallId": "tc1", "success": True, "result": {"content": "class Refund {}"}})
    ev("assistant.message", 6, {"content": "", "outputTokens": 300,
                                "toolRequests": [{"toolCallId": "tc2", "name": "create", "arguments": {"path": "/repo/claims-api/tests/RefundTests.cs"}}]})
    ev("tool.execution_complete", 7, {"toolCallId": "tc2", "success": True, "result": {"content": "Created"}})
    ev("assistant.message", 8, {"content": "", "outputTokens": 100,
                                "toolRequests": [{"toolCallId": "tc3", "name": "bash", "arguments": {"command": "dotnet test"}}]})
    ev("tool.execution_complete", 9, {"toolCallId": "tc3", "success": True, "result": {"content": "Passed!  - Failed:     0, Passed:    43"}})
    ev("session.shutdown", 10, {"modelMetrics": {"claude-sonnet-4.5": {
        "usage": {"inputTokens": 50000, "outputTokens": 500, "cacheReadTokens": 40000, "cacheWriteTokens": 5000},
        "requests": {"count": 3, "cost": 3}}}})
    lines.append("garbage")
    open("copilot-cli/abc-123/events.jsonl", "w").write("\n".join(lines) + "\n")


# ---------------- OpenTelemetry ----------------
def kv(k, v):
    if isinstance(v, bool):
        return {"key": k, "value": {"boolValue": v}}
    if isinstance(v, int):
        return {"key": k, "value": {"intValue": str(v)}}
    if isinstance(v, float):
        return {"key": k, "value": {"doubleValue": v}}
    return {"key": k, "value": {"stringValue": v}}


def otel_fixtures():
    reset("otel-claude-logs.jsonl", "otel-genai-spans.json")

    def logs(records):
        return json.dumps({"resourceLogs": [{"resource": {"attributes": [kv("service.name", "claude-code")]},
                                             "scopeLogs": [{"scope": {"name": "com.anthropic.claude_code.events"}, "logRecords": records}]}]})

    def rec(event, ts, sid="sess-otel-1", **attrs):
        a = [kv("event.name", event), kv("event.timestamp", f"2026-11-02T08:00:{ts:02d}.000Z"), kv("session.id", sid)]
        a += [kv(k, v) for k, v in attrs.items()]
        return {"timeUnixNano": "0", "body": {"stringValue": "claude_code." + event}, "attributes": a}

    lines = [
        logs([rec("user_prompt", 0, prompt_length=42, prompt="Fix the flaky premium test")]),
        logs([rec("api_request", 2, model="claude-sonnet-4-5-20250929", input_tokens=12, output_tokens=300,
                  cache_read_tokens=20000, cache_creation_tokens=4000, cost_usd=0.0156)]),
        logs([rec("tool_result", 3, name="Bash", success="true", duration_ms=900, tool_result_size_bytes=60000)]),
        logs([rec("user_prompt", 10, prompt_length=20)]),
        logs([rec("api_request", 11, model="claude-sonnet-4-5-20250929", input_tokens=5, output_tokens=50,
                  cache_read_tokens=24000, cache_creation_tokens=100, cost_usd=0.008)]),
        logs([rec("user_prompt", 1, sid="sess-otel-0", prompt_length=5)]),
        logs([rec("api_request", 1, sid="sess-otel-0", model="claude-haiku-4-5", input_tokens=5, output_tokens=5,
                  cache_read_tokens=0, cache_creation_tokens=0)]),
    ]
    open("otel-claude-logs.jsonl", "w").write("\n".join(lines) + "\n")

    def span(trace, sid, name, start, attrs):
        return {"traceId": trace, "spanId": sid, "name": name, "startTimeUnixNano": str(start), "endTimeUnixNano": str(start + 1),
                "attributes": [kv(k, v) for k, v in attrs.items()]}

    n = 1762200000000000000
    spans = [
        span("t1", "s1", "invoke_agent coder", n, {"gen_ai.operation.name": "invoke_agent", "gen_ai.conversation.id": "conv-9",
                                                   "gen_ai.input.messages": json.dumps([{"role": "user", "content": "Rename the Quote type"}])}),
        span("t1", "s2", "chat gpt-5", n + 10, {"gen_ai.operation.name": "chat", "gen_ai.conversation.id": "conv-9", "gen_ai.response.model": "gpt-5",
                                                "gen_ai.usage.input_tokens": 30000, "gen_ai.usage.cache_read.input_tokens": 20000,
                                                "gen_ai.usage.output_tokens": 700}),
        span("t1", "s3", "execute_tool edit", n + 20, {"gen_ai.operation.name": "execute_tool", "gen_ai.conversation.id": "conv-9",
                                                       "gen_ai.tool.name": "edit_file", "gen_ai.tool.call.id": "call-1",
                                                       "gen_ai.tool.call.arguments": json.dumps({"path": "src/Quote.cs"})}),
        span("t2", "s4", "chat gpt-5", n + 5_000_000_000, {"gen_ai.operation.name": "chat", "gen_ai.conversation.id": "conv-9",
                                                           "gen_ai.request.model": "gpt-5", "gen_ai.usage.input_tokens": 31000,
                                                           "gen_ai.usage.cache_read.input_tokens": 30000, "gen_ai.usage.output_tokens": 100}),
    ]
    json.dump({"resourceSpans": [{"resource": {"attributes": [kv("service.name", "my-agent")]}, "scopeSpans": [{"spans": spans}]}]},
              open("otel-genai-spans.json", "w"))


sqlite_fixtures()
cursor_fixtures()
opencode_fixtures()
copilot_cli_fixture()
otel_fixtures()
print("fixtures written to", os.path.abspath(OUT))
