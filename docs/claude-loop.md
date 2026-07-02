# Claude backlog loop — the conductor manual

The autonomous ticket loop for this repo. **Loop control is deterministic bash; every ticket runs
in its own fresh headless `claude -p` process that dies with the ticket.** There is no long-lived
orchestrator session, so nothing accumulates: per-ticket cost is flat whether you run 1 ticket or
grind the backlog all night.

## Why this shape (context economics)

Two earlier designs were retired:

1. **`/loop /ticket`** — every ticket ran in the *same* growing session. Ticket N's transcript
   stayed in context while ticket N+1 worked; per-turn input cost climbed until auto-compaction
   fired and diluted the signal.
2. **`/backlog` as an in-session orchestrator** — one session spawning a `ticket-worker` subagent
   per ticket. Better, but every worker's return still landed in the orchestrator's context, and
   every orchestrator turn re-read the whole growing history. After a long batch the "thin"
   orchestrator wasn't thin.

The fix is to make the orchestrator **not an LLM**: a script picks tickets, spawns
`claude -p "/work-ticket <n>"` per ticket, judges the machine-readable result, merges, repeats.
The only LLM context that ever exists is one sharp per-ticket session.

## Components

| Piece | Role |
|---|---|
| `scripts/claude/backlog-loop.sh` | the conductor — serial loop, lock, stop conditions, console totals |
| `scripts/claude/next-ticket.sh` | deterministic picker: priority labels + `Depends on #X` gate + skip rules |
| `scripts/claude/work-ticket.sh` | one ticket: fresh headless session → judge `STATUS:` → wait for pr-verify → squash-merge → sync main |
| `.claude/commands/work-ticket.md` | the per-ticket discipline prompt (the old `ticket-worker` agent, promoted to a slash command) |
| `.claude/commands/backlog.md` | `/backlog` in an interactive session = launch the script in background + report the roll-up |

## Usage

```bash
scripts/claude/backlog-loop.sh              # drain: run until no ready ticket is left
scripts/claude/backlog-loop.sh -n 3         # at most 3 tickets
scripts/claude/backlog-loop.sh 123 130      # exactly these tickets, in this order
scripts/claude/work-ticket.sh 123           # a single ticket, one fresh session
scripts/claude/next-ticket.sh               # dry-run the picker (prints the next ready number)
```

**Overnight (macOS):** the machine must not sleep mid-run:

```bash
caffeinate -is scripts/claude/backlog-loop.sh
```

**Cron (optional):** prefer the manual `caffeinate` run — cron on a sleeping laptop silently
skips. If the machine is awake at night anyway:

```
0 1 * * * cd ~/RiderProjects/LotroKoniecDev && /usr/bin/caffeinate -is scripts/claude/backlog-loop.sh -n 6 >> logs/claude-loop/cron.log 2>&1
```

From an interactive Claude session, `/backlog [n | issue numbers]` does the launch + final
roll-up for you. Don't edit files or switch branches while a loop runs — the working copy belongs
to the loop.

## What "ready" means (the picker)

Open issue, not `[Epic]`/`[Tracking]`, none of the skip labels, and every `Depends on #X` in the
body already CLOSED (a ticket is closed by its merged PR, so closed = merged). Order: `critical` >
`high` > `medium` > `low` > unlabeled, then lowest number first.

Default exclusions (all overridable via env):

- labels `loop-blocked`, `qa` (manual/human passes), `post-mvp` (deliberately cut from MVP),
- titles matching `^M4-` (the WPF milestone is Windows-only — it cannot build on the macOS host),
- issue `#85` (M2-18 forum watcher — deferred post-MVP; work it only by naming it explicitly).

## Knobs (env vars)

| Var | Default | Meaning |
|---|---|---|
| `LOOP_EFFORT` | `max` | claude effort per ticket |
| `LOOP_MODEL` | `opus` | Opus 4.8 — its 1M-token context window is native (no `[1m]` variant, no long-context premium) |
| `LOOP_PERMISSION_MODE` | `auto` | headless permission mode |
| `LOOP_ALLOWED_TOOLS` | git/gh/dotnet/scripts | loop-scoped Bash allowlist passed via `--allowedTools` |
| `LOOP_UNSAFE` | `0` | `1` = `--dangerously-skip-permissions` (full overnight autonomy) |
| `LOOP_MAX_BUDGET_USD` | (none) | optional per-ticket API budget cap |
| `LOOP_TICKET_TIMEOUT_MIN` | `90` | wall-clock kill switch per ticket; leftovers are stashed |
| `LOOP_CHECKS_TIMEOUT_MIN` | `30` | wait for pr-verify before falling back to `--auto` merge |
| `LOOP_SKIP_LABELS` | `loop-blocked,qa,post-mvp` | picker label exclusions |
| `LOOP_SKIP_TITLES` | `^M4-` | picker title-regex exclusion |
| `LOOP_SKIP_ISSUES` | `85` | picker number exclusions |
| `LOOP_LIMIT_SLEEP_MIN` | `60` | nap length when the usage limit is hit |
| `LOOP_LIMIT_RETRIES` | `4` | max naps before giving up |
| `LOOP_MAX_CONSECUTIVE_FAILURES` | `2` | systemic-failure circuit breaker |

Example overnight run with a hard per-ticket budget:

```bash
LOOP_MAX_BUDGET_USD=15 caffeinate -is scripts/claude/backlog-loop.sh
```

## Outcomes & triage

The loop prints one console line per outcome and a totals line at the end (counts + total cost);
triage of blocked tickets happens on GitHub via the `loop-blocked` label. Raw per-ticket session
artifacts (`ticket-<n>.json` / `.stderr` / `.meta`) land in `logs/claude-loop/<timestamp>/` for
debugging only.

Per-ticket outcomes:

- **merged** — PR squash-merged after green pr-verify (branches are kept — house rule).
- **queued** — checks outlasted the wait window; `gh pr merge --auto` queued the merge.
- **blocked** — the worker hit a genuine business question / dependency / mis-scope / red build.
  The ticket gets the `loop-blocked` label and the exact questions as an issue comment. Triage:
  `gh issue list --label loop-blocked` → answer in a comment → remove the label → the loop can
  pick it up again.
- **failed / timeout** — session error or kill switch; leftovers are stashed as
  `claude-loop salvage #<n>` (`git stash list`), main is restored. Two consecutive failures stop
  the whole loop (something systemic).
- **usage limit** — the loop naps (`LOOP_LIMIT_SLEEP_MIN`) and retries the same ticket.

## Safety model

- The runner **refuses a dirty working copy** and never deletes work — anything left behind is
  stashed, never reset.
- The worker session may commit/push/PR (that authorization is the point of loop mode) but **never
  merges**; the runner merges only after required checks pass, and never with `--delete-branch`.
- Business decisions are never invented: they come back as BLOCKED questions on the issue.
- Default permission mode is `auto` plus a loop-scoped git/gh/dotnet/scripts `--allowedTools`
  allowlist (interactive sessions are unaffected). `LOOP_UNSAFE=1` trades that for
  zero-friction full autonomy — your call per run.
- One loop at a time via `.claude/backlog-loop.lock`; a crashed run's lock is removed manually.

## Troubleshooting

- **"another loop appears to be running"** — stale lock after a crash: `rm -rf .claude/backlog-loop.lock`.
- **Ticket ended `error` with no STATUS block** — read `logs/claude-loop/<run>/ticket-<n>.json`
  (`.result` field) and `.stderr`; usually a permission denial (extend `LOOP_ALLOWED_TOOLS`) or a
  mid-run crash.
- **Checks keep timing out** — raise `LOOP_CHECKS_TIMEOUT_MIN`; pr-verify runs the integration
  suite and can be slow on cold runners.
- **The picker returns nothing but issues exist** — they're excluded (labels/titles/deps); run
  `LOOP_SKIP_LABELS= LOOP_SKIP_TITLES= LOOP_SKIP_ISSUES= scripts/claude/next-ticket.sh` to see the
  unfiltered choice, then fix labels/deps on GitHub.
