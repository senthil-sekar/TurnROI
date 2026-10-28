# TurnROI architecture

How TurnROI reads agent sessions, judges turns, and acts on what it finds. The [README](../README.md) covers usage.

## Layout

```
src/TurnRoi.Core            Library, no dependencies beyond the .NET base library
  Model/                    AgentSession → Turn → LlmCall / ToolCall, TokenUsage
  Adapters/                 One adapter per agent + SessionSources (discovery, auto-detection)
  Pricing/                  Embedded price table (prices.json), longest-prefix model matching
  Analysis/                 SessionAnalyzer (findings), Outcomes (ROI), ToolKinds, WasteProfile
  Guardrails/               GuardrailEngine (decisions), HookProtocol (per-agent payloads/answers), ReadLedger
  Apply/                    RepoGuidance (rules, skill, tuned settings), ApplyPlanner (per-agent files), ApplyRunner (backup/undo)
src/TurnRoi.Cli             `turnroi` dotnet tool: argument parsing, console/JSON reports, hook I/O
tests/TurnRoi.Core.Tests    xUnit tests; Fixtures/ holds one synthetic session per agent
```

Principles:

- **Provider-reported usage is the source of truth.** Character-based estimates (chars ÷ 4) are used only for tool output sizes, and are labelled as estimates.
- **Fail visibly when reading, fail open when acting.** Unparseable lines are counted and shown in the report. A hook that hits any error exits 0 so the agent carries on.
- **Heuristics come with evidence.** Every finding and proposal says what it is based on; none is a verdict.
- **No runtime dependencies** keeps the tool small, trimmable and AOT-friendly. JSON uses source-generated contexts.

## Normalized model

| Type | Meaning |
|---|---|
| `AgentSession` | One session file. Carries `Source`, `Cwd`, `SkippedLines`, source caveats in `Notes`, and `HasPrompts` (false for Cursor). |
| `Turn` | One human prompt plus everything the agent did before the next prompt. |
| `LlmCall` | One model call: model, `TokenUsage`, optional `BilledCost` (which overrides the price table). For Copilot, it is one model's total for the turn. |
| `ToolCall` | Name, `Target` (file path or shell command), `Range` (partial reads), result size, `ResultTail` (last 600 chars, used to read test results), `IsError`, timestamp. |
| `TokenUsage` | `Input` (uncached), `CacheWrite`, `CacheRead`, `Output`. Context = input + cache write + cache read. |

## Adapters

Every format below is internal to its vendor and undocumented, so each parser is tolerant: unknown line types are ignored and unparseable lines are counted. `SessionSources.Detect` picks the adapter from the file itself (Cursor CSV → Codex rollout → Copilot session → Claude Code).

### Claude Code

`~/.claude/projects/<project>/<session>.jsonl` (or under `$CLAUDE_CONFIG_DIR`).

- One JSON object per line. `type` is `user` or `assistant`; `isMeta` lines are skipped.
- A user message with text starts a turn; a user message with only `tool_result` blocks does not. `<system-reminder>` blocks injected by the harness are stripped from prompts.
- Streamed assistant responses span several lines with the same `message.id`; their usage is merged with a field-wise max, so each counts as one call.
- `usage`: `input_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `output_tokens`. The `<synthetic>` model (local error messages) is ignored.
- Sub-agent (`isSidechain`) calls are attributed to the parent turn; sub-agent prompts don't start turns.
- `tool_use` blocks give the name and target (`file_path`, `command`, `pattern`, …; `offset`/`limit` become `Range`). They are joined to `tool_result` blocks by id.

### GitHub Copilot (VS Code)

`<User>/workspaceStorage/<hash>/chatSessions/<id>.jsonl` and `<User>/globalStorage/emptyWindowChatSessions/`. The `<User>` folders searched are those of Code, Code - Insiders and VSCodium on each OS. `workspace.json` next to `chatSessions` gives the project folder.

- `.jsonl` is a mutation log. `{"kind":0,"v":state}` sets the whole state. Kind 1 sets a value at path `k`. Kind 2 pushes `v` onto the array at `k`, truncating to index `i` first if given. Kind 3 deletes the value at `k`. Older versions write a single `.json` snapshot.
- When both `.json` and `.jsonl` exist, the `.jsonl` is used. The same session id found in several places (VS Code copies empty-window sessions) is counted once.
- Each `requests[]` entry is a turn. `message.text` is the prompt, and the model is `result.metadata.resolvedModel` or `modelId` (with the `copilot/` prefix removed).
- Tokens come from `modelTotals[]`: `inputTokens` (includes cached), `cachedTokens` and `outputTokens`. Older records only have `promptTokens`/`completionTokens` for the last call; such turns undercount, and the report says so.
- Tools come from `response[]` items of kind `toolInvocationSerialized` (`toolId`, terminal command from `toolSpecificData.commandLine.original`). Tool output sizes aren't recorded.

### OpenAI Codex CLI

`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` (or under `$CODEX_HOME`). Each line is `{"timestamp", "type", "payload"}`.

- `session_meta` gives the id and cwd. `turn_context` gives the model in effect. `event_msg`/`user_message` starts a turn.
- `event_msg`/`token_count` has cumulative `info.total_token_usage`. Each call is the difference from the previous total, which also makes repeated events harmless. `input_tokens` includes `cached_input_tokens`.
- `function_call`, `custom_tool_call` and `local_shell_call` are joined to their `*_output` by `call_id`. A shell command `["bash","-lc","…"]` becomes `…`. `apply_patch`, whether a tool or run through the shell, is recorded as an edit of the first `*** Update/Add File:` path. Shell output JSON carries `metadata.exit_code`.

### Cursor

Cursor keeps chats in SQLite (`state.vscdb`, table `cursorDiskKV`, keys `composerData:*` and `bubbleId:*`), but recent builds write zero for per-message `tokenCount`, so local data can't attribute spend. TurnROI reads the **usage CSV export** from the Cursor dashboard instead.

- Columns are matched loosely by name: Date, Model, `Input (w/o Cache Write)`, `Input (w/ Cache Write)`, Cache Read, Output, Total, Cost. Thousands separators and quoted fields are handled.
- The two input columns are read as separate amounts unless a row's Total only adds up with "with" as a superset. In that case, cache writes are derived from the difference.
- `Cost` is used as billed. Non-numeric values such as "Included" count as $0, with a note.
- There are no prompts, so turns are approximated as bursts of requests less than 2 minutes apart. Pushback and outcome detection are off.

### Adding an agent

1. Implement `IAgentSourceAdapter` (`Name`, `CanRead`, `Read`) producing an `AgentSession`.
2. Add an `AgentSource` value, and wire it into `SessionSources` (`AdapterFor`, `Detect`, `DefaultRoots`, `FindSessions`).
3. If the agent's tool names are new, teach `ToolKinds` which ones read, edit or run shell commands.
4. Add a **synthetic** fixture to `tests/TurnRoi.Core.Tests/Fixtures` and adapter tests. Never commit real transcripts.

## Findings

`SessionAnalyzer` produces per-turn findings, each with the file or command it concerns (`Subject`):

| Finding | Rule |
|---|---|
| RepeatedRead (R) | The same file and range is read again with no edit to that file in between |
| OversizedToolResult (O) | Tool output ≥ 5k estimated tokens; ≥ 20k is high severity |
| LowCacheHit (C) | From the second turn on, a turn with ≥ 20k context tokens and < 50% from cache |
| CorrectedNextTurn (X) | The next prompt starts with pushback: no, wrong, revert, undo, stop, "that didn't work", "still failing", "[Request interrupted by user", … |
| ToolErrors (E) | 3 or more failed tool calls in a turn |

## Outcomes (ROI)

`OutcomeClassifier` decides each turn's outcome from evidence (`OutcomeEvidence`):

- **Edits**: tool calls `ToolKinds.IsEdit` recognizes (Edit/Write/MultiEdit, Copilot replace/create/insert, Codex `apply_patch`).
- **Tests**: shell commands whose first line runs a known test runner (`dotnet test`, `npm test`, `pytest`, `jest`, `vitest`, `go test`, `cargo test`, `mvn test`, `gradle test`, …). Pass/fail comes from the error flag or exit code, then from the result tail: failure counts above zero and runner failure banners mean failed; "passed", "Test Run Successful", "ok" lines mean passed.
- **Commits**: a successful `git commit` in the turn, or (with git available) any commit after the turn started that touched an edited file. `GitCliHistory` runs `git log --all --since=<session start> --name-only` once per session that has edits; `--no-git` disables it.

Decision order:

1. No tool data and no prompts → unknown.
2. Pushed back → wasted.
3. Edits:
   - last test failed → failing
   - else tests passed, or committed in the turn, or edited files committed later → productive
   - else unverified
4. No edits:
   - committed, or tests passed → productive
   - else exploratory

Spend per outcome is summed by cost when every call is priced, otherwise by tokens.

## Guardrails (hooks)

`turnroi hooks install` adds two entries to the chosen settings file, replacing any earlier TurnROI entries and leaving everything else untouched:

```json
{ "hooks": {
  "PreToolUse":  [{ "matcher": "*", "hooks": [{ "type": "command", "command": "turnroi hook pre-tool-use",  "timeout": 10 }] }],
  "PostToolUse": [{ "matcher": "*", "hooks": [{ "type": "command", "command": "turnroi hook post-tool-use", "timeout": 10 }] }]
} }
```

**Protocol.** Claude Code passes the event as JSON on stdin (`tool_name`, `tool_input`, `transcript_path`, `cwd`, and `tool_response` for PostToolUse). Exit 0 lets the call continue. Exit 2 sends stderr to the agent; on PreToolUse, it also blocks the call. Any internal error exits 0. The project folder is `$CLAUDE_PROJECT_DIR`, or failing that `cwd`; `.turnroi.json` is read from there.

**Rules** (`GuardrailEngine`, which re-parses the transcript on each call):

- **Re-read guard (PreToolUse, `rereadGuard: "block"`)** blocks a `Read` of a path and range that was already read successfully, when there has been no edit since and the file's modified time is not later than that read. The in-flight call has no result yet, so it is never mistaken for the earlier read.
- **Turn budget (`turnBudgetTokens`, default 5,000,000; or `turnBudgetUsd`)** counts the current turn's total tokens. With `budgetAction: "warn"`, PostToolUse tells the agent once per turn to wrap up; a marker file in the temp folder prevents repeats. With `"block"`, PreToolUse refuses every further tool call this turn and asks the agent to summarize and check in.
- **Output nudge (PostToolUse, `outputWarnTokens`, default 8,000)** fires when a tool response is at least that many estimated tokens, and tells the agent how to filter output or read a range next time.

### Hooks for Cursor and GitHub Copilot

`HookProtocol` translates each agent's hook format; the CLI is called as `turnroi hook <phase> --agent <agent>`, and without `--agent` the agent is detected from the payload (`conversation_id` means Cursor).

| | Claude Code | GitHub Copilot | Cursor |
|---|---|---|---|
| Config | `.claude/settings(.local).json` | `.github/hooks/turnroi.json` (VS Code agent mode; Copilot CLI/cloud agent read the same file) | `.cursor/hooks.json` (`version: 1`, `preToolUse`/`postToolUse`) |
| Payload | `tool_name`, `tool_input`, `tool_response`, `transcript_path` | VS Code: Claude-style snake_case; CLI: `toolName`, `toolArgs` (JSON string), `toolResult` | `tool_name`, `tool_input`, `tool_output`, `conversation_id`, `workspace_roots` |
| Answer | stderr + exit 2 | stderr + exit 2 | JSON on stdout: `{"permission":"allow"\|"deny","agent_message",…}`; post: `{"additional_context":…}` |

Cursor treats invalid output from a permission hook as a block, so TurnROI always prints a permission, even when it fails internally (it then allows).

Copilot's and Cursor's transcripts aren't parsed by the hooks, so the re-read guard uses a **read ledger**: a small JSON file per agent and conversation in the temp folder. Completed reads are recorded on PostToolUse; edits forget the file. The engine then sees the ledger as a session, so the rule is identical everywhere. (VS Code Copilot also runs hooks from `.claude/settings.json`; a transcript outside `~/.claude` routes those calls to the ledger too.) The turn budget needs token counts, which only Claude Code transcripts provide.

## Apply: rules, skill and settings per agent

`turnroi profile --apply --agent <agents>` learns from the sessions run in the project folder (`--dir`, default the current folder), unless `--project` or an explicit file is given. It builds `RepoGuidance`:

- **Rules** (always on, a few lines): quiet ways to run the noisiest commands (≥ 10k tokens of output), "don't re-read unchanged files" when re-reads occurred, "run the tests before finishing" when ≥ 10% of spend was failing or ≥ 30% unverified, and "plan before multi-file changes" when ≥ 15% was wasted.
- **Skill** `repo-efficiency` (loaded on demand): quiet invocations (`Quiet.For`: `dotnet test --nologo -v q`, `pytest -q`, `npm ci --silent`, …; otherwise `… 2>&1 | tail -n 40`) and outlines of re-read files (`Outline.Of`: type/function declarations or Markdown headings with line numbers, up to 40 entries). Outlines are stamped with the HEAD commit, and the skill tells the agent to search instead of trusting line numbers if the file changed since.
- **Settings**: `.turnroi.json`, with the turn budget tuned to 4× the median turn once there are ≥ 10 turns.

`ApplyPlanner` writes them in each agent's own place:

| | Claude Code | Cursor | GitHub Copilot |
|---|---|---|---|
| Rules | Block in `CLAUDE.md` | `.cursor/rules/turnroi.mdc` (`alwaysApply: true`) | Block in `.github/copilot-instructions.md` |
| Skill | `.claude/skills/repo-efficiency/SKILL.md` | `.cursor/skills/…` | `.github/skills/…` |
| Hooks | `.claude/settings.local.json` (`--shared`: `settings.json`) | `.cursor/hooks.json` | `.github/hooks/turnroi.json` |

Ownership rules:

- Shared files get a block between `<!-- turnroi:begin … -->` and `<!-- turnroi:end -->` that is replaced on each run.
- Settings files get TurnROI's hook entries replaced and everything else kept.
- Whole files are written only when absent or already generated by TurnROI; anything else is reported as skipped.

`ApplyRunner` copies every file it will touch to `.turnroi/backup/<timestamp>/`, with a manifest, before writing; `.turnroi/` gets its own `.gitignore`. `--undo` restores the newest backup: old content comes back, created files are deleted along with any folders left empty. Without `--yes`, the CLI previews and asks; with redirected input it only previews.

## Waste profile and compare

`WasteProfile.Build` aggregates session reports: outcome totals, noisiest commands (shell output grouped by the first two words of the command), files re-read without changes (count and number of sessions), low-cache turns, largest turn and most common test command. Proposals are rule-based:

| Proposal | Trigger |
|---|---|
| Trim a command's output | One command's output totals ≥ 20k tokens |
| Block re-reads | ≥ 5 re-reads or ≥ 10k re-read tokens |
| Summarize a file in CLAUDE.md | The same file is re-read unchanged in ≥ 3 sessions |
| Agree on a plan first | ≥ 15% of spend wasted |
| Verify before stopping | ≥ 10% failing or ≥ 30% unverified |
| Keep the prompt prefix stable | Low-cache turns ≥ 20% of turns (with at least 5 turns) |
| Set a turn budget | A turn used ≥ 5M tokens |

`compare --split <date>` builds a profile for each side of the date and reports cost and tokens per turn, outcome shares (in percentage points) and cache hit.

## Pricing

`prices.json` holds USD per million tokens for input, output, cache write (5-minute) and cache read, keyed by model-id prefix. The longest prefix wins, and dots and hyphens are treated alike (`claude-sonnet-4.5` matches `claude-sonnet-4-5`). A billed cost from the source (Cursor) takes precedence. Unknown models make costs `n/a`; the report lists them so the user can pass `--prices`.

## JSON report

`turnroi analyze --json` emits a versioned document (`schemaVersion`, currently 2). It has session metadata, usage, total cost, `notes`, and `turns[]` (usage, cost, tools, `outcome`, `evidence`). It also has `outcomes[]` (per-outcome turns/cost/tokens) and `findings[]`. Breaking changes bump `schemaVersion`.

## Known limits

- Outcome signals are heuristics. A reverted change can still have been useful exploration, and pushback detection reads only the start of the next prompt.
- Copilot doesn't record tool output sizes. Older Copilot records hold only the last call's tokens.
- Cursor has no prompts or tools in its export. Joining it with local `state.vscdb` prompts needs SQLite, which is not yet included.
- Not yet supported: Copilot CLI sessions as an analysis source, OpenCode, OpenTelemetry GenAI exports, an MCP server.
- Cursor's usage export has no tool calls, so rules and skills for Cursor have to be learned from another agent's history (`--source … --agent cursor`).
- Hook payload details for Cursor and Copilot are based on their published docs; field names are read tolerantly and anything unrecognized fails open.
