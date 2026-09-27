---
description: Autonomous backlog loop — launch the HEADLESS per-ticket loop (scripts/claude/backlog-loop.sh; one fresh `claude -p` process per ticket, each in its own worktree, up to 3 at once) and report the PRs it opened. It never merges — the owner reviews, assigns themselves, and runs /merge-train.
argument-hint: [issue numbers | count | (empty = every ready ticket)] [-j N]
---

> **Maintainer-only.** This command drives `scripts/claude/backlog-loop.sh`, which pushes branches
> and opens PRs. It needs a `gh` session with write access to this repository — from a fork it
> fails at the first write. Contributors want `/ticket` instead.
> See [`scripts/claude/README.md`](../../scripts/claude/README.md).

You are the loop **conductor's assistant**, not the orchestra. The actual loop is
`scripts/claude/backlog-loop.sh`: deterministic bash runs each ticket in a **fresh headless
`claude -p` session**, inside its own worktree cut from `origin/main`, up to `-j` at a time. Each
session dies with its ticket. Nothing accumulates in YOUR context — your only jobs are launch, wait,
and report. (The old pattern — spawning ticket subagents from this session — piled every worker's
results into one growing context; never do it.)

**The loop stops at the PR (ADR-0060).** It never merges. The owner reads each PR, approves it by
assigning themselves (GitHub does not let an author approve their own PR), and merges the approved
batch with `/merge-train`, which refuses any PR that is not approved that way.

You are ALLOWED to intervene if you see the loop failing, so it can actually run and work.

## 1. Launch

Map `$ARGUMENTS` onto the script:

- issue numbers → `scripts/claude/backlog-loop.sh <numbers>` — exactly those. This is the normal
  use: the owner picks independent tickets in a recon session and hands them over.
- a count `N` → `scripts/claude/backlog-loop.sh -n N` (the next N ready tickets from the picker)
- empty → `scripts/claude/backlog-loop.sh` (every ready ticket)
- `-j N` passes through (default 3 at once; `-j 1` runs them one by one)

Run it via Bash with `run_in_background: true`, wrapped in `caffeinate -is` unless the user says
not to. If it exits at once with a lock message, or refuses because `scripts/claude/` differs from
`origin/main`, surface that to the user and stop — never force it. A checkout on a branch cut
before ADR-0060 runs the OLD conductor, which merges: if `scripts/claude/work-ticket.sh` here
still contains `gh pr merge`, stop and tell the user to bring this checkout up to date with `main`.

## 2. While it runs

Stay thin. Do not read diffs, do not implement, do not review, do not poll in a tight loop — you
are re-invoked when the background script exits. If the user asks for progress, check the
background task's console output and relay the last few `[loop]`/`[conductor]` lines.
You basically only intervene if the script fails somehow.

The main checkout is not the loop's: every ticket works in `.claude/worktrees/ticket-<n>`. You and
the user may keep working in the main checkout while the loop runs — just never inside a
`ticket-<n>` worktree the loop owns.

## 3. Report the roll-up

When the script finishes, report from its console output:

- the **PRs opened**, as the roll-up table prints them (ticket, PR, checks, CodeQL alerts) — this
  is the owner's review queue,
- tickets **blocked** — relay each ticket's open questions **verbatim** (they were posted as issue
  comments; `gh issue list --label loop-blocked` finds them),
- tickets **skipped** (already had an open PR or a worktree) and **refused** (provenance gate),
- failures/timeouts with a one-line cause each (dig into `logs/claude-loop/<run>/ticket-<n>.json`
  only when the console line isn't enough),
- total cost for the run, and the next step: review each PR, assign yourself to approve it, then
  `/merge-train`.

## Guardrails

- **Never work a ticket inline in this session** and never spawn per-ticket subagents — that is
  the exact context-ballooning anti-pattern this command replaces.
- **Never merge and never assign.** The assignee is the owner's approval; setting it for them would
  approve work nobody read.
- One loop at a time — the script's lock (`.claude/backlog-loop.lock`) enforces it; don't delete
  the lock unless the user confirms the previous run is dead.
