# TurnROI

**Was that agent turn worth its tokens?**

Coding agents routinely spend 100k–200k+ tokens per turn. Existing tools either compress context or report totals. TurnROI asks a different question: *which turns paid off, which didn't, and why?*

> Status: **Phase 0 spike.** Expect breaking changes.

## Supported agents

| Agent | Data TurnROI reads | Per-turn prompts | Tokens | Cost | Tool calls |
|---|---|---|---|---|---|
| **Claude Code** | Local transcripts, `~/.claude/projects/**/*.jsonl` | Yes | Per model call, incl. cache read/write | Price table | Yes, with output size |
| **GitHub Copilot (VS Code)** | Local chat sessions, `<VS Code User>/workspaceStorage/*/chatSessions/*.jsonl` (and legacy `.json`) | Yes | Per turn, per model; cached split out | Price table (API-equivalent; Copilot bills premium requests) | Names and terminal commands; no output size |
| **Cursor** | Usage CSV exported from the Cursor dashboard | No: turns are approximated as bursts of requests | Per request, incl. cache read/write | **As billed** ("Included" rows = $0) | No |

Why Cursor uses the CSV: Cursor stores chats locally in SQLite (`state.vscdb`), but recent builds write zero for per-message token counts, so the dashboard export is the only reliable source of tokens and billed cost. Combining it with local prompts is on the roadmap.

All three formats are internal to their vendors and undocumented; TurnROI parses them tolerantly and reports unparseable lines rather than failing.

## What it does today

```
$ turnroi analyze latest

Summary
  Turns 3 · LLM calls 9 · Models claude-sonnet-4-5-20250929, claude-haiku-4-5-20251001
  Context tokens 269,645  (cache read 81% · cache write 17% · uncached 1%)
  Output tokens  1,340
  Est. cost      $0.27
  Spend on turns you pushed back on: $0.20 (77%)

Turns with model calls (2)
  #  Prompt                                  Calls  Tools  Context  Cache  Output   Cost  Flags
  1  Add validation for negative claim amo…      5      4     127k    64%     740  $0.20  ROX
  2  No, that's wrong - negative amounts a…      4      2     143k    96%     600  $0.06

Findings (3)
  HIGH T1   Next prompt pushed back: "No, that's wrong - negative amounts are allowed f…"  ~127k tok
  HIGH T1   Bash (dotnet test) returned ~22,500 tokens  ~23k tok
  WARN T1   Re-read of unchanged file .../src/ClaimValidator.cs (read #2 since last edit)  ~2.0k tok
```

- **Per-turn spend:** groups every model call and tool call under the human prompt that caused it, including sub-agent calls.
- **Cache-aware cost:** separates uncached input, cache writes and cache reads, because a 200k context that is 95% cache reads costs far less than it looks.
- **Waste findings:**

  | Flag | Finding | Why it matters |
  |---|---|---|
  | `R` | Re-read of a file with no edit in between | Paid twice for the same content |
  | `O` | Tool output over ~5k tokens | Logs and test output dumped into context |
  | `C` | Low prompt-cache hit on a large turn | Context churn breaking the cache |
  | `X` | Your next prompt pushed back ("no", "revert", "still failing", interrupted) | First proxy for "this turn wasn't worth it" |
  | `E` | 3+ failed tool calls in a turn | Agent flailing |

Every finding is a heuristic with its evidence attached, not a verdict.

## Install and run

Requires the .NET 8 SDK or later.

```bash
git clone https://github.com/senthil-sekar/TurnROI.git
cd TurnROI
alias turnroi="dotnet run --project src/TurnRoi.Cli --"

# Claude Code
turnroi sessions                          # recent sessions
turnroi analyze latest                    # most recent session
turnroi analyze <id|path> --all           # every turn in order

# GitHub Copilot in VS Code (stable, Insiders and VSCodium are searched)
turnroi sessions --source copilot
turnroi analyze latest --source copilot

# Cursor: export usage as CSV from the Cursor dashboard, then
turnroi analyze ~/Downloads/usage-events.csv

# Any source, machine-readable
turnroi analyze latest --json > report.json
```

A file path is auto-detected; `--source` is only needed for `latest` or a session id. Override search folders with `--root`. As a global tool (once published): `dotnet tool install -g TurnROI`.

## Pricing

A small price table ships embedded (`src/TurnRoi.Core/Pricing/prices.json`). Models not in the table are reported as **unpriced** instead of guessed. Supply your own with `--prices my-prices.json` in the same format. Verify prices against your provider; they change.

## Privacy

Local-only. TurnROI reads transcripts on your machine and sends nothing anywhere. Transcripts contain your code and prompts, so never commit real ones as test fixtures; fixtures in this repo are synthetic.

## Design

```
src/TurnRoi.Core
  Model/        Normalized session → turn → LLM call / tool call
  Adapters/     IAgentSourceAdapter + Claude Code, Copilot (VS Code), Cursor (usage CSV); SessionSources discovery
  Pricing/      Prefix-matched price table, unpriced rather than guessed
  Analysis/     SessionAnalyzer → per-turn cost + findings
src/TurnRoi.Cli   Zero-dependency CLI (console table and versioned JSON)
tests/            xUnit tests against synthetic fixtures (one per source)
```

Principles: provider-reported usage is the source of truth; estimates are labelled; parsing failures are counted and shown, never silently dropped; no runtime dependencies.

## Roadmap

1. **Ledger (now):** per-turn spend and waste findings for Claude Code.
2. **ROI:** join turns to outcomes: did the diff survive to commit, did tests pass, was it reverted.
3. **Fast loop:** Claude Code hooks for in-session guardrails (output caps, re-read blocking, budget circuit breaker).
4. **Slow loop:** per-repo waste profile → proposed config changes you approve.
5. **More agents:** Copilot CLI, Codex, OpenCode, OpenTelemetry GenAI exports; Cursor prompts from local `state.vscdb` joined to the usage CSV.

Every intervention will be benchmarked for cost *at equal task success*: removing waste, not moving it.

## Contributing

New agent support is an adapter: implement `IAgentSourceAdapter`, add a synthetic fixture, add tests. Issues and PRs welcome.

## License

MIT
