# Changelog

## 0.1.0

First release.

- **Analyze:** per-turn spend for Claude Code, GitHub Copilot (VS Code), OpenAI Codex CLI and Cursor (usage CSV), with cache-aware cost and waste findings: re-reads, oversized output, low cache hit, pushback, tool errors.
- **ROI outcomes:** each turn is classified as productive, unverified, failing, wasted or exploratory from edits, test results, commits (including later commits found in git history) and pushback.
- **Guardrails:** Claude Code hooks block re-reads of unchanged files, nudge after oversized output, and warn or block when a turn passes its budget. Install with `turnroi hooks install`.
- **Profile:** waste patterns across sessions with proposed fixes, optionally written as Markdown.
- **Compare:** before/after metrics around a date.
- `--json` output with a versioned schema.
