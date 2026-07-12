---
description: Autonomous backlog loop — launch the HEADLESS per-ticket loop (scripts/claude/backlog-loop.sh; one fresh `claude -p` process per ticket) and report the roll-up. Replaces the old in-session subagent orchestrator, whose context ballooned with every ticket.
argument-hint: [count | issue numbers | (empty = drain all ready tickets)]
---

You are the loop **conductor's assistant**, not the orchestra. The actual loop is
`scripts/claude/backlog-loop.sh`: deterministic bash picks each ready ticket and runs it in a
**fresh headless `claude -p` session** that dies with the ticket. Nothing accumulates in YOUR
context — your only jobs are launch, wait, and report. (The old pattern — spawning ticket
subagents from this session — piled every worker's results into one growing context; never do it.)

You are ALLOWED to intervene if you see loop failing, so it can actually run and work.

## 1. Launch

Map `$ARGUMENTS` onto the script:

- empty → `scripts/claude/backlog-loop.sh` (drain: run until no ready ticket remains)
- a count `N` → `scripts/claude/backlog-loop.sh -n N`
- issue numbers → `scripts/claude/backlog-loop.sh <numbers>` (exactly those, in order)

Run it via Bash with `run_in_background: true`. If it exits immediately with a dirty-working-copy
or stale-lock message, surface that to the user and stop — never force it.

We should technically caffeinate these commands unless the user explicitly says not to do it.

## 2. While it runs

Stay thin. Do not read diffs, do not implement, do not review, do not poll in a tight loop — you
are re-invoked when the background script exits. If the user asks for progress, check the
background task's console output and relay the last few `[loop]`/`[conductor]` lines.
You basically only intervene if the script fails somehow. 

## 3. Report the roll-up

When the script finishes, report from its console output:

- tickets **merged** (with PR links from the `[loop] … MERGED PR #n` lines) and any **auto-merge
  queued**,
- tickets **blocked** — relay each ticket's open questions **verbatim** (they were posted as issue
  comments; `gh issue list --label loop-blocked` finds them),
- failures/timeouts with a one-line cause each (dig into `logs/claude-loop/<run>/ticket-<n>.json`
  only when the console line isn't enough),
- total cost for the run, and the suggested next action.

## Guardrails

- **Never work a ticket inline in this session** and never spawn per-ticket subagents — that is
  the exact context-ballooning anti-pattern this command replaces.
- One loop at a time — the script's lock (`.claude/backlog-loop.lock`) enforces it; don't delete
  the lock unless the user confirms the previous run is dead.
- The working copy belongs to the loop while it runs: don't edit files or switch branches here
  until it finishes.
