# TurnROI architecture

How TurnROI reads agent sessions, judges turns, and acts on what it finds. The [README](../README.md) covers usage.

## Layout

```
src/TurnRoi.Core            Library, no dependencies beyond the .NET base library
  Model/                    AgentSession → Turn → LlmCall / ToolCall, TokenUsage
  Adapters/                 One adapter or store per agent + SessionSources (discovery, auto-detection)
  Storage/                  SqliteReader: read-only SQLite (with WAL) in pure C#, for Cursor and OpenCode
  Pricing/                  Embedded price table (prices.json), version-aware model matching
  Analysis/                 SessionAnalyzer (findings), Pushback, Outcomes (ROI), ToolKinds, WasteProfile
  Guardrails/               GuardrailEngine, HookProtocol (per-agent payloads/answers), ReadLedger, InterventionLog
  Apply/                    RepoGuidance (rules, skill, settings), ApplyPlanner, ApplyRunner (backup/undo), SkillFreshness
  Bench/                    BenchSuite, BenchRunner (variants × tasks in fresh repo copies), Shell
  Team/                     TeamExport (numbers-only summaries), TeamRollup
  Evaluation/               TurnLabel, LabelStore, Evaluator (precision/recall against hand labels)
src/TurnRoi.Cli             `turnroi` dotnet tool: arguments, console/JSON/HTML reports, hook I/O, MCP server
tests/TurnRoi.Core.Tests    xUnit tests; Fixtures/ holds synthetic sessions (tests/fixture-generator/generate.py)
```

Principles:

- **Provider-reported usage is the source of truth.** Character-based estimates (chars ÷ 4) are used only for tool output sizes, and are labelled as estimates.
- **Fail visibly when reading, fail open when acting.** Unparseable lines are counted and shown in the report. A hook that hits any error exits 0 so the agent carries on.
- **Heuristics come with evidence.** Every finding and proposal says what it is based on; none is a verdict.
- **No runtime dependencies** keeps the tool small, trimmable and AOT-friendly. JSON uses source-generated contexts.

## Normalized model

| Type | Meaning |
|---|---|
| `AgentSession` | One session file. Carries `Source`, `Cwd`, `SkippedLines`, source caveats in `Notes`, and `HasPrompts` (false for Cursor's usage CSV). |
| `Turn` | One human prompt plus everything the agent did before the next prompt. |
| `LlmCall` | One model call: model, `TokenUsage`, optional `BilledCost` (overrides the price table) and `ReportedCost` (used when the model isn't priced). For Copilot, it is one model's total for the turn. |
| `ToolCall` | Name, `Target` (file path or shell command), `Range` (partial reads), result size, `ResultTail` (last 600 chars, used to read test results), `IsError`, timestamp. |
| `TokenUsage` | `Input` (uncached), `CacheWrite`, `CacheRead`, `Output`. Context = input + cache write + cache read. |

## Adapters

Every format below is internal to its vendor and undocumented, so each parser is tolerant: unknown line types are ignored and unparseable lines are counted. `SessionSources.Detect` picks the adapter from the file itself (Cursor CSV or `.vscdb` → `opencode.db` → OTLP JSON → Codex rollout → Copilot CLI → Copilot → Claude Code). `SessionSources.List/Resolve/Read` work with a `SessionRef` (source, id, location), so file-per-session sources and database-backed ones (Cursor, OpenCode, OTel exports) are handled alike.

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

Two sources, which can be combined:

- **Local chats** in `<User>/globalStorage/state.vscdb` (`~/.config/Cursor/User`, `~/Library/Application Support/Cursor/User`, `%APPDATA%\Cursor\User`). Table `cursorDiskKV`: `composerData:<id>` holds the chat (`fullConversationHeadersOnly` lists bubble ids in order; older chats inline a `conversation` array), and `bubbleId:<chat>:<bubble>` holds each message (`type` 1 = user, 2 = assistant; `text`; `toolFormerData` with `name`, `params`, `result`, `status`). The project folder comes from `composer.composerHeaders` (global) or each workspace's `composer.composerData` plus `workspace.json`. Prompts, tools, targets, output sizes and exit codes are all there. Recent builds write zero in `tokenCount`, so tokens usually aren't.
- **Usage CSV** from the Cursor dashboard: tokens and billed cost per request, no prompts or tools. Columns are matched loosely by name; the two input columns are read as separate amounts unless the row's Total only adds up with "with cache write" as a superset. Non-numeric cost ("Included") counts as $0, with a note. Alone, turns are bursts of requests less than 2 minutes apart, and outcomes are unknown.

`turnroi analyze <chat> --source cursor --usage usage.csv` joins them: each CSV row is assigned to the turn whose time window (prompt until the next prompt, the last one ending 10 minutes after its last message) contains it. Concurrent chats can be mixed together; the report says so.

### OpenCode

`~/.local/share/opencode/opencode.db` (or `$XDG_DATA_HOME/opencode`): tables `session`, `message`, `part`, each with a JSON `data` column merged with its plain columns. Older versions keep the same objects as JSON files under `storage/`. User messages' text parts (minus `synthetic` ones) start turns. Assistant messages carry `tokens{input, output, reasoning, cache{read, write}}` (reasoning counts as output), `modelID` and `cost`; `cost` is used when the model isn't in the price table. Tool parts give `tool`, `state.input`, `state.output`, `state.status` and `metadata.exit`.

### GitHub Copilot CLI

`~/.copilot/session-state/<id>/events.jsonl` (or `$COPILOT_HOME`): `{type, data, timestamp}` per line. `session.start` (cwd), `session.model_change`, `user.message` (turn), `assistant.message` (`toolRequests`, `outputTokens`), `tool.execution_start/complete` (`success`, `result.content`). When per-call `assistant.usage` events exist they are used; otherwise `session.shutdown.modelMetrics` session totals are spread over each model's responses in proportion to output tokens, with a note. Cached tokens are subtracted from input only when they fit inside it (OpenAI-style counts include them; Anthropic-style don't).

### OpenTelemetry exports

OTLP JSON files (one document, or one per line as the file exporter writes them), selected with `--source otel` or detected from `resourceSpans`/`resourceLogs`:

- **GenAI semantic-convention spans**: `gen_ai.operation.name` `chat` spans become calls (`gen_ai.usage.input_tokens` includes cached tokens; `cache_read.input_tokens` and `cache_creation.input_tokens` are taken out), `execute_tool` spans become tools (`gen_ai.tool.name`, arguments for the target). Sessions are `gen_ai.conversation.id`; turns are prompts from `invoke_agent` input messages, or one per trace.
- **Claude Code log events**: `user_prompt` (turn), `api_request` (`input_tokens`, `cache_read_tokens`, `cache_creation_tokens`, `output_tokens`, `cost_usd`), `tool_result` (`name`, `success`, `tool_result_size_bytes`), grouped by `session.id`.

### SQLite without a dependency

`Storage/SqliteReader` reads SQLite files directly: header, b-tree pages (table and index, interior and leaf), overflow chains, record decoding, the schema from `sqlite_master`, and the WAL (only frames up to the last commit whose salts and cumulative checksums verify). Key-prefix lookups use the matching index (including `UNIQUE` auto-indexes) with a range scan, falling back to a full scan. It opens files read-only with sharing, so Cursor can keep running. UTF-8 databases only.

### Adding an agent

1. Implement `IAgentSourceAdapter` (`Name`, `CanRead`, `Read`) producing an `AgentSession`.
2. Add an `AgentSource` value, and wire it into `SessionSources` (`Parse`, `AdapterFor` or `Read`, `Detect`, `DefaultRoots`, `List`). Sources kept in a database get a store class like `CursorLocalStore` instead of a file adapter.
3. If the agent's tool names are new, teach `ToolKinds` which ones read, edit or run shell commands.
4. Add a **synthetic** fixture (binary ones via `tests/fixture-generator/generate.py`) and adapter tests. Never commit real transcripts.

## Findings

`SessionAnalyzer` produces per-turn findings, each with the file or command it concerns (`Subject`):

| Finding | Rule |
|---|---|
| RepeatedRead (R) | The same file and range is read again with no edit to that file in between |
| OversizedToolResult (O) | Tool output ≥ 5k estimated tokens; ≥ 20k is high severity |
| LowCacheHit (C) | From the second turn on, a turn with ≥ 20k context tokens and < 50% from cache |
| CorrectedNextTurn (X) | The next prompt pushes back (`Pushback.Detect`, below) |
| ToolErrors (E) | 3 or more failed tool calls in a turn |

**Pushback** looks at the first 200 characters of the next prompt. Openers ("no", "nope", "wrong", "revert", "undo", "stop doing…", "wait,", an interruption) and phrases anywhere in that span ("that's not what I asked", "didn't work", "still failing", "same error", "you deleted…", "I said…", "don't touch…", "doesn't compile") count. Polite openers and plain instructions don't ("no worries", "no need to…", "no tests exist for X", "stop the dev server"). A pasted compiler error, stack trace or failing test summary counts only when the previous turn edited code. `turnroi label` and `turnroi eval` measure it against people's judgement.

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
| Answer | stderr + exit 2 | JSON on stdout, exit 0: `permissionDecision`/`permissionDecisionReason`/`additionalContext` plus the same in Claude's `hookSpecificOutput` | JSON on stdout: `{"permission":"allow"\|"deny","agent_message",…}`; post: `{"additional_context":…}` |
| Turn start | transcript | `UserPromptSubmit` hook | `beforeSubmitPrompt` hook, and `generation_id` changes |

Cursor treats invalid output from a permission hook as a block, so TurnROI always prints a permission, even when it fails internally (it then allows).

Copilot's and Cursor's transcripts aren't parsed by the hooks, so the re-read guard uses a **read ledger**: a small JSON file per agent and conversation in the temp folder. Completed reads are recorded on PostToolUse; edits forget the file. The ledger also counts tool calls in the current turn, which resets at the prompt hook (or a new Cursor `generation_id`). The engine sees the ledger as a session, so the rules are identical everywhere.

- **Turn budget** is checked in order: USD, tokens, then tool calls (`turnBudgetToolCalls`, default 100), so every agent has a working budget. Copilot also gets token counts: the hook reads its chat session file (`transcript_path`, or the session id under `chatSessions`).
- **De-duplication**: VS Code Copilot also runs hooks from `.claude/settings.json`, so the same call can arrive twice. Each `(phase, session, tool_use_id)` is handled once.

**Intervention log.** Every block, nudge and budget warning is appended to `interventions.jsonl` in the data folder (`$TURNROI_HOME`, else the OS local app-data folder `turnroi`), with the rule, target, model and, for blocked re-reads, the tokens avoided (the earlier read's size ÷ 4). `turnroi savings` summarizes it per rule; cost avoided is priced at the model's input rate, a floor, since the content would also have stayed in context for later calls. Set `"logInterventions": false` to turn it off.

## Apply: rules, skill and settings per agent

`turnroi profile --apply --agent <agents>` learns from the sessions run in the project folder (`--dir`, default the current folder), unless `--project` or an explicit file is given. It builds `RepoGuidance`:

- **Rules** (always on, a few lines): quiet ways to run the noisiest commands (≥ 10k tokens of output), "don't re-read unchanged files" when re-reads occurred, "run the tests before finishing" when ≥ 10% of spend was failing or ≥ 30% unverified, and "plan before multi-file changes" when ≥ 15% was wasted.
- **Skill** `repo-efficiency` (loaded on demand): quiet invocations (`Quiet.For`: `dotnet test --nologo -v q`, `pytest -q`, `npm ci --silent`, …; otherwise `… 2>&1 | tail -n 40`) and outlines of re-read files (`Outline.Of`: type/function declarations or Markdown headings with line numbers, up to 40 entries). Outlines are stamped with the HEAD commit, and the skill tells the agent to search instead of trusting line numbers if the file changed since.
- **Settings**: `.turnroi.json`, with the turn budget tuned to 4× the median turn once there are ≥ 10 turns.

Each outline sits between `<!-- turnroi:outline path=… sha=… -->` and `<!-- turnroi:outline-end -->`, where `sha` is the first 8 hex of the SHA-256 of the file's content with line endings normalized. `turnroi profile --check` compares those hashes with the files (exit 1 when stale, for CI or a pre-commit hook); `--refresh` rewrites only the stale outlines, drops those of deleted files, and updates the commit note, with the usual backup and undo.

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

`prices.json` holds USD per million tokens for input, output, cache write (5-minute) and cache read, keyed by model id. Matching ignores a provider prefix (`anthropic/`), treats dots and hyphens alike, and rewrites Cursor's version-first names (`claude-4.5-sonnet-thinking` → `claude-sonnet-4-5-thinking`). The longest key wins only if what follows it is nothing, a date, or a word variant (`-thinking`, `-codex`, `-latest`); a further version number or a separately priced tier (`-pro`, `-mini`, …) doesn't match, so a new model never borrows an older one's price. Cost comes from a billed amount (Cursor CSV) first, then the table, then a cost the source reported (OpenCode, Copilot CLI, Claude Code OTel). Unknown models make costs `n/a` and are listed so the user can pass `--prices`.

## JSON report

`turnroi analyze --json` emits a versioned document (`schemaVersion`, currently 2). It has session metadata, usage, total cost, `notes`, and `turns[]` (usage, cost, tools, `outcome`, `evidence`). It also has `outcomes[]` (per-outcome turns/cost/tokens) and `findings[]`. Breaking changes bump `schemaVersion`.

## MCP server

`turnroi mcp` speaks the Model Context Protocol over stdio (newline-delimited JSON-RPC 2.0: `initialize`, `ping`, `tools/list`, `tools/call`). Its tools are read-only and run the matching CLI command with output captured:

| Tool | Runs |
|---|---|
| `list_sessions` | `sessions` |
| `analyze_session` | `analyze` (default `latest`, so an agent can review its own session; `json: true` returns schema v2) |
| `waste_profile` | `profile` (never `--apply`) |
| `guardrail_savings` | `savings` |
| `check_skill_freshness` | `profile --check` |

A failing command returns `isError: true` with its message; a malformed request gets a JSON-RPC error, and the server keeps running.

## Benchmark

`turnroi bench suite.json` answers "does this variant cost less at the same success rate?". For each run (`runs` × task × variant, variants interleaved so drift hits all of them), it copies the repo to a temp folder (skipping `bin`, `obj`, `node_modules`, …), runs the variant's setup commands, runs the agent command (`{prompt}` is replaced with the shell-quoted prompt, also in `$TURNROI_PROMPT`), then the task's verify command (exit 0 = success). The run's session is found among the source's sessions modified since the run started whose `cwd` ends with the copy's unique folder name, and analyzed for tokens and cost. The summary reports success, cost, cost per success, and cost on the (task, run) pairs every variant solved, which is the fair comparison. See [BENCHMARK.md](BENCHMARK.md).

## Team rollup

`turnroi export` writes a `TeamExport` (schema 1): per session, a hashed id, source, project (folder name, or its hash with `--hash-projects`), start time, turns, calls, models, token totals, cost, cost and turns per outcome, and finding counts and tokens per kind. No prompts, paths, commands or code. `turnroi team <folder>` merges exports (one per person; the same session exported twice counts once) and reports by person and by project.

## Labels and evaluation

`turnroi label` walks a session's turns and records your verdict (or `--set 3=wasted`) in `labels.jsonl` in the data folder, with where the session lives and what TurnROI predicted. `turnroi eval` re-analyzes each labelled session with the current detector (falling back to the saved prediction if it's gone) and prints accuracy, per-outcome precision, recall and F1, and a confusion matrix. It's how changes to pushback and outcome rules are checked.

## Known limits

- Outcome signals are heuristics. A reverted change can still have been useful exploration; pushback detection reads only the opening of the next prompt.
- Copilot (VS Code) doesn't record tool output sizes. Older Copilot records hold only the last call's tokens. Copilot CLI without per-call usage spreads session totals.
- Cursor's local chats usually lack tokens; add `--usage` for spend. Joining by time can mix chats that ran at the same moment.
- Hook payloads for Cursor and Copilot follow their published docs; fields are read tolerantly and anything unrecognized fails open.
- Benchmarks of real agents are noisy and cost money: use several tasks and runs.
