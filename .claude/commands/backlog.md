---
description: Backlog loop — give the owner the one terminal command that starts the HEADLESS per-ticket loop (scripts/claude/start-loop.sh; one fresh `claude -p` process per ticket, each in its own worktree, up to 3 at once), and after the run report the roll-up from its saved console log. It never starts the loop from a session, and it never merges — the owner reviews, assigns themselves, and runs /merge-train.
argument-hint: [issue numbers [-j N] | -n N] — no arguments = the roll-up of the last run
---

> **Maintainer-only.** The command this prints runs `scripts/claude/backlog-loop.sh`, which pushes
> branches and opens PRs. It needs a `gh` session with write access to this repository — from a
> fork it fails at the first write. Contributors want `/ticket` instead.
> See [`scripts/claude/README.md`](../../scripts/claude/README.md).

You are the loop **conductor's assistant**, not the orchestra. The actual loop is
`scripts/claude/backlog-loop.sh`: deterministic bash runs each ticket in a **fresh headless
`claude -p` session**, inside its own worktree cut from `origin/main`, up to `-j` at a time. Each
session dies with its ticket. Nothing accumulates in YOUR context — your only jobs are to hand the
owner the command, and to report when the run is over. (The old pattern — spawning ticket subagents
from this session — piled every worker's results into one growing context; never do it.)

**The loop never runs from a Claude Code session — not this one, not any other (#969).** Since
Claude Code 2.1.285, a command that the Bash tool runs in the background is killed after two hours
at most, with a SIGKILL to the whole process tree; a foreground call ends at its own timeout. No
trap runs then: the lock stays, no worker salvages its work, no resume marker is written, and the
next run skips those tickets. A normal batch runs five hours or more. So the owner starts the loop
in a plain terminal with `scripts/claude/start-loop.sh`, and this command only prints that line and,
after the run, reads what the run left behind.

**The loop stops at the PR (ADR-0060).** It never merges. The owner reads each PR, approves it by
assigning themselves (GitHub does not let an author approve their own PR), and merges the approved
batch with `/merge-train`, which refuses any PR that is not approved that way.

Both sections below need the main checkout. Every log, the lock and the ticket worktrees live there,
whichever checkout this session runs in. Find it once, then write its path out in every later
command: a variable does not survive from one Bash call to the next.

```bash
dirname "$(git rev-parse --path-format=absolute --git-common-dir)"
```

Below, `<MAIN>` stands for that path.

## 1. With arguments — print the terminal command

Map `$ARGUMENTS` onto the launcher's arguments:

- issue numbers → `<numbers>` — exactly those. This is the normal use: the owner picks independent
  tickets in a recon session and hands them over.
- `-n N`, or "the next N" → `-n N` (the next N ready tickets from the picker)
- `-j N` passes through (default 3 at once; `-j 1` runs them one by one)

Without ticket numbers and without `-n N` (N above zero), the loop would take every ready ticket,
so the launcher refuses that: ask which tickets instead of printing a line.

Write out one line in a `bash` block, with the launcher's full path so that it works from any
folder: `<MAIN>/scripts/claude/start-loop.sh <arguments>`. Whichever copy of the launcher runs, it
hands the run over to the copy on `origin/main`, so any copy that exists will do:

- `<MAIN>/scripts/claude/start-loop.sh`;
- if the main checkout is on a branch older than the launcher, the copy in the loop's own checkout,
  `<MAIN>-loop/scripts/claude/start-loop.sh` (it is on `origin/main` after every run);
- if neither exists yet, give the one-time setup first, and then the line with the `-loop` copy.
  When `<MAIN>-loop` does not exist:
  `git -C <MAIN> fetch origin main && git -C <MAIN> worktree add --force --detach <MAIN>-loop origin/main`.
  When it exists, check first that `git -C <MAIN>-loop status --short` prints nothing (the loop
  checkout must stay clean), then:
  `git -C <MAIN> fetch origin main && git -C <MAIN>-loop checkout --detach origin/main`.

Never print a path inside `.claude/worktrees/`: the loop removes those folders.

Before you print it, check one thing: a loop that already runs. When `<MAIN>/.claude/backlog-loop.lock/pid`
names a live process whose command line contains `backlog-loop.sh`, say so — the launcher would
refuse, and `/backlog` with no arguments shows its progress.

Then tell the owner, in a few short lines:

- the loop runs in that terminal, from its own checkout next to the main one (`<MAIN>-loop`), moved
  to `origin/main` first — the main checkout may stay on any branch, and they may keep working in
  it; only the `.claude/worktrees/ticket-<n>` folders belong to the loop;
- Ctrl-C, or closing the terminal, stops the run, and each worker salvages its work first;
- the console is copied to `<MAIN>/logs/claude-loop/console-<timestamp>.log`, and `/backlog` with no
  arguments gives the roll-up after the run (or the progress during it).

Then stop. **Do not run the command** — not in the background, not in the foreground, not behind
`nohup` or `&`.

## 2. No arguments — the roll-up of the last run

1. Find the newest console copy: `ls <MAIN>/logs/claude-loop | grep '^console-' | sort | tail -1`.
   The names carry the start time, so they sort by name; a plain `console-*.log` glob fails in zsh
   when nothing matches. None → say that no run started with `start-loop.sh` was found, and stop.
2. Its `[conductor] run <folder>` line names the run folder, which holds one `ticket-<n>.meta` per
   ticket (and `ticket-<n>.json` / `.stderr` for each session). A console copy without that line
   belongs to a run that is still starting when a conductor runs (the lock check in section 1):
   say so and stop. With no conductor running, it is a start the conductor refused — a wrong
   argument, or another loop that took the lock first: relay its last lines and stop. (The
   launcher's own refusals come before it makes a console copy, so the owner already saw them in
   the terminal.)
3. Check that it is the last run: the newest run folder
   (`ls <MAIN>/logs/claude-loop | grep '^[0-9]' | sort | tail -1`) should be the one the console
   copy names. A newer one is a run started with `backlog-loop.sh` directly, which keeps no console
   copy: say so, and go on with that run folder. Step 4 decides whether it still runs; once it has
   ended, build its roll-up from its `.meta` files, as for a stopped run below.
4. **Still running** — the lock's owner is alive (the check in section 1), or a worker of this run
   still ends its session: `ps -Ao pid,command | grep '[w]ork-ticket.sh'` lists a process whose
   command line ends with the run folder. The conductor removes its lock as soon as it has told its
   workers to stop, and each worker may take another 20 seconds or so to salvage. Report the
   progress only — the tickets started (`── start #<n>` lines), the ones finished (their
   `[loop] #<n>` outcome lines) and the last few lines — and say the roll-up comes when the run
   ends. Stop.
5. **Ended.** Where the table comes from depends on how it ended:
   - The copy has a `[conductor] done:` line: the conductor printed its table above it. Use that
     table.
   - No `done:` line: the run was stopped (Ctrl-C, a closed terminal) or killed. A stopped conductor
     prints no table, so build one row per ticket from the `.meta` files: `key=value` lines, and the
     last line of a key wins (`issue`, `outcome`, `pr`, `resumes`, `worktree`, `session`). A `.meta`
     with no `outcome=` line belongs to a worker that was killed hard (SIGKILL, power loss): its
     worktree may still hold work, it has no resume marker, and the next run will skip that ticket —
     list it as one that needs the owner. A ticket the run line names (`· tickets: …`) with no
     `.meta` at all never started: list it as **not started**, since those are the ones to run again.
6. Report:
   - the **PRs opened** — ticket, PR, checks, CodeQL alerts. This is the owner's review queue. The
     table shows the checks as they stood when the run ended, so read them again now:
     `gh pr checks <pr>`, and the open alerts with
     `gh api "repos/{owner}/{repo}/code-scanning/alerts?ref=refs/pull/<pr>/merge&state=open" --jq length`,
   - tickets **blocked** — relay each ticket's open questions **verbatim** (they were posted as issue
     comments; `gh issue list --label loop-blocked` finds them),
   - tickets **skipped** (already had an open PR or a worktree) and **refused** (`untrusted` — the
     provenance gate),
   - tickets whose row says **`worktree kept: run #<n> again to resume its session`** (`outcome=limit`
     with `worktree=kept`) — the usage limit outlasted every nap; the session waits in its worktree,
     and naming the ticket in the next run resumes it,
   - tickets **stopped**, failed or timed out, with a one-line cause each (dig into
     `ticket-<n>.json` or `.stderr` only when the console line isn't enough),
   - the total cost: the `done:` line has it. For a run without one, add up `total_cost_usd` over
     the run folder's result files, one file at a time, so that a file cut short by a stop only
     drops its own share:
     `find <run folder> -name 'ticket-*.json' -exec jq -r '.total_cost_usd // 0' {} \; 2>/dev/null | awk '{ s += $1 } END { printf "%.2f\n", s }'`,
   - the next step: review each PR, assign yourself to approve it, then `/merge-train`.

## Guardrails

- **Never start the loop from a session**, and never restart a run that ended. When the console copy
  shows a systemic failure, find the cause in the logs, tell the owner what to fix, and give them
  the command again.
- **Never work a ticket inline in this session** and never spawn per-ticket subagents — that is
  the exact context-ballooning anti-pattern this command replaces.
- **Never merge and never assign.** The assignee is the owner's approval; setting it for them would
  approve work nobody read.
- One loop at a time — the conductor's lock (`.claude/backlog-loop.lock`) enforces it; don't delete
  the lock unless the owner confirms the previous run is dead.
