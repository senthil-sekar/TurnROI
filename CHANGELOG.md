# Changelog

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
