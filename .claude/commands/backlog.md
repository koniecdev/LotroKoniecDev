---
description: Autonomous backlog loop — spawn one isolated ticket-worker subagent per ticket (fresh context each), merge between, keep the orchestrator thin. The cheap, context-safe replacement for `/loop /ticket`.
argument-hint: [count | issue numbers | (empty = next ready ticket)]
---

You are the **backlog orchestrator**. Drive the ticket loop while staying **thin**: decide order,
spawn one **`ticket-worker`** subagent per ticket (each gets its own fresh context), merge clean
PRs, and keep only each worker's compact summary.

**You never read full diffs, never implement, never review.** That work lives and dies inside each
worker's isolated context. That separation is the whole point: it is the cost/context win over
`/loop /ticket`, which piled every ticket into one ballooning context. After N tickets your context
holds N short summaries — not N transcripts.

## 1. Build the work-list

Interpret `$ARGUMENTS`:
- issue numbers → use exactly those, in the given order.
- a count `N` → take the next `N` ready tickets.
- empty → take the single next ready ticket.

"Ready" = open AND its dependencies are merged. `gh issue list --state open`; for each candidate
read `Depends on #X` — if a dependency is unmerged, the ticket is **not ready** (skip it, note
why). Order by milestone/number (the M2 backlog is dependency-ordered). The M2-18 forum watcher
(#85) is deferred post-MVP — skip it unless explicitly named.

## 2. State the plan

List the tickets you'll run, in order, in 2-3 lines. Then proceed — **entering `/backlog` IS the
standing authorization** to commit → push → PR → **merge** per ticket (the interactive "ask before
pushing" rule is waived for the loop, per CLAUDE.md → Loop mode).

## 3. Per ticket — spawn, judge, merge

Serial, one at a time (so the working copy is never shared between two tickets). For each ticket:

1. **Spawn** the `ticket-worker` agent (Task tool, `subagent_type: ticket-worker`): *"Work ticket
   #<n> end-to-end per your discipline. Return DONE + PR url + summary, or BLOCKED + questions."*
2. **Judge the return:**
   - **DONE (clean PR):** merge it — `gh pr merge <n> --squash` (**never `--delete-branch`** —
     branches are kept, see CLAUDE.md house rules) — then `git checkout main && git pull` so the
     next worker branches off fresh main. Keep only the worker's summary.
   - **BLOCKED (open business questions):** do NOT guess. Surface the questions to the user
     verbatim. Then skip to the next *independent* ready ticket, or stop if none is independent —
     your call, stated plainly.
   - **BLOCKED (unmet dependency / mis-scope / red build / unclean review):** report it, do **not**
     merge, and stop unless a later ticket is genuinely independent.
3. Move on.

## 4. Roll-up

When the batch ends (list exhausted, blocked with nothing independent left, or user interrupt),
report: tickets merged (with PR links), tickets blocked (with the exact questions/blockers the user
must resolve), and the suggested next action.

## Guardrails

- **Stay thin.** Don't open diffs, don't re-implement, don't re-review — trust the worker's summary
  plus the merged PR. If you catch yourself reading source files, you've defeated the purpose.
- **Serial, clean working copy.** Never run two workers concurrently here — that's the separate
  worktrees-per-ticket upgrade, not this command. One ticket fully closed before the next.
- **Never merge broken work.** Red build or non-APPROVE review → stop, don't merge.
- **Never invent business answers.** A BLOCKED ticket waits for the user — that is both the worker's
  prime directive and yours.
