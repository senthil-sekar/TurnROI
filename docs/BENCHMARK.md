# Benchmarking TurnROI

`turnroi bench` runs the same tasks with the same agent under several variants, each in a fresh copy of your repo, and checks each run with a command. It answers one question: **does a variant cost less at the same success rate?**

## A suite file

```json
{
  "repo": "../claims-api",
  "agent": "claude -p {prompt} --permission-mode acceptEdits",
  "source": "claude",
  "verify": "dotnet test --nologo -v q",
  "runs": 3,
  "timeoutMinutes": 20,
  "variants": {
    "baseline": [],
    "turnroi":  ["turnroi profile --apply --agent claude --yes --project claims-api"]
  },
  "tasks": [
    { "id": "negative-amounts", "prompt": "Reject negative claim amounts except refunds; add tests." },
    { "id": "rename-quote",     "prompt": "Rename Quote to PolicyQuote across the solution." },
    { "id": "fix-flaky",        "prompt": "Make PremiumCalculatorTests.Rounds_half_up deterministic.", "verify": "dotnet test --filter PremiumCalculatorTests" }
  ]
}
```

| Field | Meaning |
|---|---|
| `repo` | Copied for every run (relative to the suite file). `bin`, `obj`, `node_modules`, `.vs`, `.idea` are skipped; change with `exclude`. |
| `agent` | Runs the agent once, non-interactively, inside the copy. `{prompt}` becomes the shell-quoted prompt; `$TURNROI_PROMPT`, `$TURNROI_TASK` and `$TURNROI_VARIANT` are also set. |
| `source` | Where that agent writes its sessions (`claude`, `codex`, `copilot-cli`, `opencode`, …), so each run can be measured. `sessionsRoot` overrides the location. |
| `verify` | Exit code 0 means the task succeeded. A task's own `verify` wins. Without one, the agent's exit code decides. |
| `variants` | Name → setup commands run in the copy before the agent. |
| `runs` | Repeats of every task × variant. Override with `--runs`. |

Non-interactive commands for common agents (check your version's flags):

| Agent | `agent` |
|---|---|
| Claude Code | `claude -p {prompt} --permission-mode acceptEdits` |
| OpenAI Codex CLI | `codex exec --full-auto {prompt}` (`"source": "codex"`) |
| GitHub Copilot CLI | `copilot -p {prompt} --allow-all-tools` (`"source": "copilot-cli"`) |
| OpenCode | `opencode run {prompt}` (`"source": "opencode"`) |

## Running it

```bash
turnroi bench suite.json                      # every variant
turnroi bench suite.json --variant turnroi    # one variant
turnroi bench suite.json --runs 5 --write runs.json
```

```
Benchmark
  Variant   Success  Cost    Tokens      Cost / success  Cost on shared successes
  baseline  8/9      $14.20  21,904,113  $1.78           $12.10 (3 task(s))
  turnroi   8/9      $10.95  16,388,720  $1.37           $9.40 (3 task(s))

  turnroi vs baseline: -22% cost on the tasks both solved, success 8/9 vs 8/9.
```

**Cost on shared successes** sums only the (task, run) pairs every variant solved. It's the fair comparison: a variant that gives up early looks cheap per run but not here.

## Reading the result

- Agents are noisy. Use at least 3 tasks and 3 runs before trusting a difference of less than ~20%.
- Variants run interleaved (task 1 baseline, task 1 turnroi, task 2 baseline, …), so model or cache changes during the run hit all of them.
- Real runs cost real money and change files only inside the temp copies. The copies are deleted afterwards.
- A run whose session can't be found shows a note; check `source` and `sessionsRoot`, and that the agent writes `cwd` into its sessions.
