# TurnROI

**Was that agent turn worth its tokens?**

AI coding agents can spend 200k+ tokens on a single turn. TurnROI shows where that spend went, which turns actually paid off, and what to change so the next session wastes less. It works with Claude Code, GitHub Copilot (VS Code), OpenAI Codex CLI and Cursor, runs locally, and sends nothing anywhere.

```
$ turnroi analyze latest

Where the spend went
  Outcome       Cost  Share  Turns  Meaning
  wasted       $0.20    77%      1  your next prompt pushed back
  exploratory  $0.06    23%      1  read/answered; no code changed
  77% of spend went to turns that were pushed back on or left tests failing.

Most expensive turns
  #  Prompt                                   Context  Cache   Cost  Outcome      Flags
  1  Add validation for negative claim amo…      127k    64%  $0.20  wasted       ROX

Findings
  HIGH T1   Next prompt pushed back: "No, that's wrong - negative amounts are allowed f…"
  HIGH T1   Bash (dotnet test) returned ~22,500 tokens
  WARN T1   Re-read of unchanged file .../src/ClaimValidator.cs (read #2 since last edit)
```

## Install

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) or later.

```bash
git clone https://github.com/senthil-sekar/TurnROI.git
cd TurnROI
dotnet pack src/TurnRoi.Cli -c Release -o ./nupkg
dotnet tool install -g TurnROI --add-source ./nupkg
```

## Use it

**1. See where a session's spend went**

```bash
turnroi analyze latest                         # most recent Claude Code session
turnroi analyze latest --source copilot        # or copilot, codex
turnroi analyze ~/Downloads/usage-events.csv   # Cursor: export usage CSV from the Cursor dashboard
turnroi sessions --source codex                # list recent sessions
```

Every turn gets an outcome:

| Outcome | Meaning |
|---|---|
| productive | Changed code, and tests passed or it was committed |
| unverified | Changed code, with no test run or commit seen |
| failing | Changed code, and the last test run failed |
| wasted | Your next prompt pushed back ("no", "revert", "still failing", interrupted) |
| exploratory | Read, searched or answered without changing code |

Flags point at the waste inside a turn: **R** re-read an unchanged file, **O** oversized tool output, **C** low prompt-cache hit, **X** pushed back, **E** repeated tool errors.

**2. Fix it: rules, a skill and guardrails for your agent**

```bash
turnroi profile                                   # recurring waste across sessions + proposed fixes
turnroi profile --apply --agent claude            # or cursor, copilot, all
turnroi profile --undo                            # revert the last change
```

`--apply` learns from this project's sessions and writes three things in each agent's native format. It previews every file and asks before writing anything:

| | Claude Code | Cursor | GitHub Copilot |
|---|---|---|---|
| Always-on rules (a few lines) | Block in `CLAUDE.md` | `.cursor/rules/turnroi.mdc` | Block in `.github/copilot-instructions.md` |
| `repo-efficiency` skill | `.claude/skills/` | `.cursor/skills/` | `.github/skills/` |
| Guardrail hooks | `.claude/settings.local.json` | `.cursor/hooks.json` | `.github/hooks/turnroi.json` |

- **Rules** come from your history. Example: "Run `dotnet test` quietly (`dotnet test --nologo -v q`)", or "propose a plan before multi-file changes" when you push back often.
- **The skill** loads only when relevant. It holds quiet versions of your noisiest commands and outlines, with line numbers, of the files the agent keeps re-reading, so it reads just the part it needs.
- **The hooks** enforce rules live (below).

Cursor's usage export has no tool calls, so rules and skills for Cursor come from your other agents' history (e.g. `--source copilot --agent cursor`); hooks work regardless.

TurnROI only edits its own marked block, its own hook entries and files it generated, and backs up everything it touches. Run it again whenever you like to refresh the rules and skill.

**3. Guardrails while the agent works**

| Guardrail | Default | What happens |
|---|---|---|
| Re-read guard | on | Blocks reading a file the agent already has, unchanged (all three agents) |
| Output nudge | 8k tokens | After a huge tool output, tells the agent to filter next time |
| Turn budget | 5M tokens, warn | Claude Code only: tells the agent to wrap up and check in, or blocks with `"budgetAction": "block"` |

Settings live in `.turnroi.json`. To install or remove only the hooks, run `turnroi hooks install --agent cursor` or `turnroi hooks uninstall --agent cursor`.

**4. Check that a change helped**

```bash
turnroi compare --split 2026-11-01 --project claims-api
```

Compares cost per turn, outcome shares and cache hits before and after a date, such as the day you installed the hooks.

## Supported agents

| Agent | Analyze reads | `--apply` / hooks | Notes |
|---|---|---|---|
| Claude Code | `~/.claude/projects` transcripts | ✓ / ✓ | Fullest data: every model call and tool output size |
| GitHub Copilot (VS Code) | VS Code chat sessions | ✓ / ✓ | Copilot bills premium requests, so cost is the API-equivalent price |
| Cursor | Usage CSV from the Cursor dashboard | ✓ / ✓ | Cost as billed; the export has no prompts, so outcomes are unknown |
| OpenAI Codex CLI | `~/.codex/sessions` rollouts | — | Its history can drive `--apply` for the other agents |

Costs are estimates from token counts and a built-in price table; models it doesn't know are shown as unpriced rather than guessed. Use `--prices <file>` to supply your own. Run `turnroi --help` for every option, and `--json` for machine-readable output.

## Privacy

Everything stays on your machine. Session files contain your code and prompts, so don't attach real ones to issues.

## Learn more

- [Architecture](docs/ARCHITECTURE.md): how each agent's data is read, how outcomes and findings are decided, and how to add an agent
- [Changelog](CHANGELOG.md)

## Contributing

Issues and PRs welcome. Adding an agent means writing one adapter, a synthetic fixture and tests (see [Architecture](docs/ARCHITECTURE.md#adding-an-agent)).

## License

MIT
