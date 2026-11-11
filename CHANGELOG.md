# Changelog

## 0.3.0

- **New sources:** Cursor's local chats (`state.vscdb`, with prompts, tools and outcomes; `--usage` joins the dashboard CSV for tokens and billed cost), GitHub Copilot CLI, OpenCode, and OpenTelemetry exports (GenAI spans and Claude Code log events). SQLite is read by a small built-in reader, so there are still no dependencies.
- **Guardrails everywhere:** the turn budget now works for Cursor and Copilot (tool-call budget, plus real token counts for Copilot), turns start at the prompt hook, Copilot gets its JSON answer format, and duplicate calls from two hook configs are handled once.
- **`turnroi savings`:** every block and nudge is logged; see tokens and cost avoided per guardrail.
- **`turnroi profile --check | --refresh`:** detect and update stale file outlines in the generated skill.
- **`turnroi mcp`:** an MCP server so agents can query their own session's spend, the waste profile and savings.
- **`turnroi bench`:** run the same tasks with and without TurnROI and compare cost at equal success ([guide](docs/BENCHMARK.md)).
- **`turnroi export` / `turnroi team`:** numbers-only exports and a team rollup by person and project.
- **`turnroi label` / `turnroi eval`:** label turns yourself and measure TurnROI's accuracy against your labels.
- **Better pushback detection:** more phrasings, fewer false alarms on polite openers, and pasted build errors after an edit.
- **`--html`** for analyze, profile, savings and team: self-contained reports with light and dark themes.
- **Prices** updated for Claude 5.x and GPT-5.x; a newer model version never borrows an older one's price; Cursor-style model names are recognized.

## 0.2.0

- **`turnroi profile --apply`** turns the waste profile into each agent's native files:
  - always-on rules: a managed block in `CLAUDE.md` and `.github/copilot-instructions.md`, and `.cursor/rules/turnroi.mdc`
  - a `repo-efficiency` skill (quiet versions of noisy commands, and outlines of often re-read files stamped with the commit)
  - tuned `.turnroi.json`
  - hooks

  It previews every file, asks before writing, and never overwrites files it didn't create. **`--undo`** restores the backup.
- **Hooks for Cursor and GitHub Copilot** (`.cursor/hooks.json`, `.github/hooks/turnroi.json`), with each agent's own payload and answer format. A per-conversation read ledger gives the re-read guard the same behavior everywhere.
- `turnroi hooks install|uninstall|print --agent claude|cursor|copilot|all`.

## 0.1.0

First release.

- **Analyze:** per-turn spend for Claude Code, GitHub Copilot (VS Code), OpenAI Codex CLI and Cursor (usage CSV), with cache-aware cost and waste findings: re-reads, oversized output, low cache hit, pushback, tool errors.
- **ROI outcomes:** each turn is classified as productive, unverified, failing, wasted or exploratory from edits, test results, commits (including later commits found in git history) and pushback.
- **Guardrails:** Claude Code hooks block re-reads of unchanged files, nudge after oversized output, and warn or block when a turn passes its budget. Install with `turnroi hooks install`.
- **Profile:** waste patterns across sessions with proposed fixes, optionally written as Markdown.
- **Compare:** before/after metrics around a date.
- `--json` output with a versioned schema.
