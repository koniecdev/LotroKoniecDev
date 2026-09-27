# Claude backlog loop — the conductor manual

The autonomous ticket loop for this repo. **Loop control is deterministic bash; every ticket runs
in its own fresh headless `claude -p` process, in its own git worktree, and the process dies with
the ticket.** There is no long-lived orchestrator session, so nothing accumulates: per-ticket cost
is flat whether you run 1 ticket or grind the backlog all night.

**The loop stops at the pull request (ADR-0060).** It never merges. The normal flow is:

1. In an ordinary session, pick the tickets you want (for example every open `type-bug` +
   `area-auth` ticket that touches different files) — `/preflight` helps.
2. Hand the numbers to the loop: `/backlog 812 830 842`, or `scripts/claude/backlog-loop.sh 812 830 842`.
   It runs up to three at once, each in `.claude/worktrees/ticket-<n>` cut from `origin/main`,
   and ends with a table of the PRs it opened.
3. Read each PR. When you are happy with one, **assign yourself** — that is the approval, because
   GitHub does not let you approve your own PR.
4. When the batch is read, run **`/merge-train`**. It merges only PRs assigned to you, and only if
   nothing was pushed to the PR after you assigned yourself.

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
`claude -p "/work-ticket <n>"` per ticket, judges the machine-readable result, repeats.
The only LLM context that ever exists is one sharp per-ticket session.

Until ADR-0060 the script also squash-merged each PR as soon as `pr-verify` went green. That made
sense for overnight runs nobody read. It stopped making sense once the owner started reading every
PR: a PR merged before anyone read it could only be reviewed after the fact. Now the loop
produces PRs, and the one merge path is `/merge-train` over PRs the owner approved.

## Components

| Piece | Role |
|---|---|
| `scripts/claude/backlog-loop.sh` | the conductor — up to `-j` tickets at once, lock, stop conditions, roll-up table of PRs |
| `scripts/claude/next-ticket.sh` | deterministic picker: priority labels + `Depends on #X` gate + skip rules + "no open PR yet" |
| `scripts/claude/issue-trust.sh` | the provenance gate: refuses an issue written by anyone without write access (ADR-0026) |
| `scripts/claude/work-ticket.sh` | one ticket: provenance gate → skip if already in flight → worktree from `origin/main` → fresh headless session → judge `STATUS:` → confirm the PR exists → remove the clean worktree |
| `.claude/commands/work-ticket.md` | the per-ticket discipline prompt (the old `ticket-worker` agent, promoted to a slash command) |
| `.claude/commands/backlog.md` | `/backlog` in an interactive session = launch the script in background + report the roll-up |
| `~/.claude-account1/skills/merge-train/` | the maintainer's merge path — merges PRs the owner approved by assigning themselves (lives outside this repo) |
| `scripts/tests/claude-loop-{provenance,conductor}.tests.sh` | offline self-tests; both run in `pr-verify` and `ci` |

## Usage

```bash
scripts/claude/backlog-loop.sh 123 130 131  # exactly these tickets, up to 3 at once (the normal use)
scripts/claude/backlog-loop.sh -j 1 123 130 # one at a time, in this order
scripts/claude/backlog-loop.sh -n 3         # the next 3 ready tickets from the picker
scripts/claude/backlog-loop.sh              # drain: every ready ticket
scripts/claude/work-ticket.sh 123           # a single ticket, one fresh session, one worktree
scripts/claude/next-ticket.sh               # dry-run the picker (prints the next ready number)
```

**Why three at once, and not one.** Each ticket works in its own worktree, so tickets never share
files, and the token cost per ticket is the same either way — only the wall clock changes. What
the tickets do share is this machine: the CPU for builds and the Docker daemon for the test
containers. Three is the global cap on parallel sessions, and it is what the owner already runs by
hand. Two things keep parallel runs honest: tickets you pick should touch different areas (two
tickets that edit the same file give the second PR a conflict at merge time), and each E2E suite
tags its Docker images per worktree (#884), so two runs never test each other's build.

**Overnight (macOS):** the machine must not sleep mid-run:

```bash
caffeinate -is scripts/claude/backlog-loop.sh
```

**Cron (optional):** prefer the manual `caffeinate` run — cron on a sleeping laptop silently
skips. If the machine is awake at night anyway:

```
0 1 * * * cd ~/RiderProjects/LotroKoniecDev && /usr/bin/caffeinate -is scripts/claude/backlog-loop.sh -n 6 >> logs/claude-loop/cron.log 2>&1
```

From an interactive Claude session, `/backlog [issue numbers | n] [-j N]` does the launch + final
roll-up for you. The loop never touches the main checkout, so you can keep working there while it
runs — just stay out of the `ticket-<n>` worktrees it owns.

## What "ready" means (the picker)

Open issue, not `[Epic]`/`[Tracking]`, none of the skip labels, **written only by trusted
maintainers** (see the provenance gate below), **no open PR yet** (a branch named `<n>-…` with an
open PR means the ticket waits for your review), and every `Depends on #X` in the body already
CLOSED (a ticket is closed by its merged PR, so closed = merged). A dependency with an open PR is
therefore not ready either: its dependent waits until you merge it. Order: `priority-critical` >
`priority-high` > `priority-medium` > `priority-low` > unlabeled, then lowest number first — but
priority never outranks provenance. Full label taxonomy: `docs/labels.md`.

Default exclusions (all overridable via env):

- labels `loop-blocked`, `epic` (a tracking parent has no work of its own), `question`, `wontfix`,
  `invalid`, `duplicate` (not decided work), `qa` (manual/human passes), `qa-blocked`, `audit`
  (audit findings are triaged by a human — name one explicitly to work it), `post-mvp`
  (deliberately cut from MVP) — the same set TheKittySaver uses, with this repo's parking label
  in place of its `post-v1`. An epic is caught **twice over**: the `epic` label is on the skip
  list, and the jq also drops any title starting `[Epic]`/`[Tracking]` regardless of
  `LOOP_SKIP_TITLES`. Either signal alone is enough — until #787 no epic here carried the label
  and the title test was what kept all five out,
- titles matching `^M4-` (the desktop-app milestone — Avalonia per ADR-0033 — targets the Windows
  patcher runtime; its E2E criterion cannot run on the macOS host),
- issue `#85` (M2-18 forum watcher — deferred post-MVP; work it only by naming it explicitly).

## The provenance gate (ADR-0026)

This repo is **public**: anyone can open an issue or comment on one, and `/work-ticket` reads the
title, body *and comments* as its instructions, then pushes a branch and opens a PR under the
owner's account. Your review before `/merge-train` is a second line of defence (ADR-0060), not a
reason to drop the first: every path into the worker still asks `issue-trust.sh` first:

> An issue is trusted only when its author **and every one of its commenters** has an
> `author_association` of `OWNER` / `MEMBER` / `COLLABORATOR` (i.e. write access), or a login
> listed in `LOOP_TRUSTED_LOGINS`.

It **fails closed** — a missing association or any GitHub API failure refuses the ticket. The gate
runs inside `work-ticket.sh`, not just the picker, so `backlog-loop.sh 123` and a bare
`work-ticket.sh 123` are gated too; a refused ticket exits `11` and no session is ever spawned.
A gate that cannot *reach* the API is systemic rather than a property of the ticket, so it surfaces
as the ordinary error exit `3` and the conductor's circuit breaker stops the run.
A stranger's harmless "+1" comment will therefore park a ticket: read it yourself, then run that
one ticket with `LOOP_TRUST_GATE=0`, or add the commenter to `LOOP_TRUSTED_LOGINS`.

## Knobs (env vars)

| Var | Default | Meaning |
|---|---|---|
| `LOOP_EFFORT` | worker effort from `~/.claude/model-policy.env`, else `high` | claude effort per ticket (`xhigh` in the maintainer policy since 2026-09-22) |
| `LOOP_MODEL` | worker model from `~/.claude/model-policy.env`, else `opus` | Opus 5.5 in the maintainer policy since 2026-09-22 (before: Opus 5 from 2026-08-05, Fable 5 from 2026-07-17) |
| `LOOP_PERMISSION_MODE` | `auto` | headless permission mode |
| `LOOP_CONFIG_DIR` | `~/.claude-account1` | Claude config dir = which account runs the loop (exported as `CLAUDE_CONFIG_DIR`) |
| `LOOP_ALLOWED_TOOLS` | git/gh/dotnet/scripts | loop-scoped Bash allowlist passed via `--allowedTools` |
| `LOOP_UNSAFE` | `0` | `1` = `--dangerously-skip-permissions` (full overnight autonomy) |
| `LOOP_MAX_BUDGET_USD` | (none) | optional per-ticket API budget cap |
| `LOOP_PARALLEL` | `3` | tickets at once (same as `-j`) |
| `LOOP_TICKET_TIMEOUT_MIN` | `90` | wall-clock kill switch per ticket; leftovers are committed on a `loop-salvage/…` branch |
| `LOOP_KEEP_WORKTREE` | `0` | `1` = keep `.claude/worktrees/ticket-<n>` after the run (by default a clean worktree is removed; the branch always stays) |
| `LOOP_GH_USER` | `koniecdev` | gh account whose token backs the loop's gh write calls (labels, issue comments); an existing `GH_TOKEN` in the environment wins |
| `LOOP_SKIP_LABELS` | `loop-blocked,epic,qa,post-mvp,audit` | picker label exclusions |
| `LOOP_SKIP_TITLES` | `^M4-` | picker title-regex exclusion |
| `LOOP_SKIP_ISSUES` | `85` | picker number exclusions |
| `LOOP_TRUSTED_ASSOCIATIONS` | `OWNER,MEMBER,COLLABORATOR` | provenance gate: associations that carry write access |
| `LOOP_TRUSTED_LOGINS` | (none) | provenance gate: extra logins (a second maintainer account, a bot) |
| `LOOP_TRUST_GATE` | `1` | `0` = skip the provenance gate **for the whole run** (you read the issue *and* its comments yourself) — use it only on a single explicitly-named ticket |
| `LOOP_LIMIT_SLEEP_MIN` | `60` | nap length when the usage limit is hit |
| `LOOP_LIMIT_RETRIES` | `8` | max naps before giving up (a limit hit at the start of a 5h usage window needs up to ~5h of naps) |
| `LOOP_MAX_CONSECUTIVE_FAILURES` | `2` | systemic-failure circuit breaker |

Example overnight run with a hard per-ticket budget:

```bash
LOOP_MAX_BUDGET_USD=15 caffeinate -is scripts/claude/backlog-loop.sh
```

## Outcomes & triage

The loop prints one console line per outcome, then a table with one row per ticket (outcome, PR,
checks, open CodeQL alerts) and a totals line (counts + total cost). The table is your review
queue. Triage of blocked tickets happens on GitHub via the `loop-blocked` label. Raw per-ticket
session artifacts (`ticket-<n>.json` / `.stderr` / `.meta`) land in `logs/claude-loop/<timestamp>/`
for debugging only.

Per-ticket outcomes:

- **pr-opened** — the worker reported DONE and the PR exists. The loop does not wait for
  `pr-verify`: the table shows the checks as they stand at the end of the run, and `/merge-train`
  checks them again before any merge.
- **skipped** — the ticket already has an open PR, or `.claude/worktrees/ticket-<n>` already
  exists (a manual `/ticket` session or an earlier run is on it). Nothing is started.
- **blocked** — the worker hit a genuine business question / dependency / mis-scope / red build.
  The ticket gets the `loop-blocked` label and the exact questions as an issue comment. Triage:
  `gh issue list --label loop-blocked` → answer in a comment → remove the label → the loop can
  pick it up again.
- **failed / timeout** — session error or kill switch; leftovers are committed on a dedicated
  `loop-salvage/<n>-<timestamp>` branch (never stash — ordinary named git history). Two
  consecutive failures stop new starts (something systemic); the running tickets finish first.
- **no-worktree** — `git fetch` or `git worktree add` failed. That is the machine, not the ticket,
  so the loop starts nothing more.
- **usage limit** — the loop starts nothing new, lets the running tickets finish, naps
  (`LOOP_LIMIT_SLEEP_MIN`) and runs the limited tickets again.
- **untrusted** — the ticket failed the provenance gate; it is skipped without spawning a session
  and without counting toward the failure circuit breaker (drain mode never selects one anyway).

## Safety model

- **Only maintainer-written text becomes a task** — the provenance gate above, enforced in front of
  the session so no invocation path bypasses it. Self-tested by
  `scripts/tests/claude-loop-provenance.tests.sh`, which runs in `pr-verify` and `ci`.
- Each ticket gets its **own worktree** from `origin/main`; the main checkout is never touched,
  and the runner never deletes work — anything left behind is committed on a dedicated
  `loop-salvage/<n>-<timestamp>` branch, never stashed, never reset. Only a clean worktree is
  removed, together with the E2E images tagged for it; the branch always stays.
- The worker session may commit/push/PR (that authorization is the point of loop mode). **Nothing
  in the loop merges or assigns** (ADR-0060). The merge path is `/merge-train`, and it takes only
  PRs the owner assigned to themselves after the last push, with green required checks and zero
  open CodeQL alerts — and it never deletes the branch.
- Stopping the conductor (Ctrl-C, `kill`, closing the terminal) stops every running worker and
  its session; `scripts/tests/claude-loop-conductor.tests.sh` pins that, together with the slot
  count, the retry after a usage limit and the stop conditions.
- Business decisions are never invented: they come back as BLOCKED questions on the issue.
- Default permission mode is `auto` plus a loop-scoped git/gh/dotnet/scripts `--allowedTools`
  allowlist (interactive sessions are unaffected). `LOOP_UNSAFE=1` trades that for
  zero-friction full autonomy — your call per run.
- One loop at a time via `.claude/backlog-loop.lock`. The lock records its owner PID, so a crashed
  run's lock is **reclaimed automatically** by the next conductor — a dead run can no longer block
  the loop forever (it once ate a whole scheduled night). A lock whose owner is still alive is
  still refused, and a refused *start* now fires the same macOS notification a finished run does,
  so a scheduled loop can never fail silently into a log file.

## Troubleshooting

- **"another loop is running (pid N)"** — a live conductor owns the lock; `ps -p N` to see it. A
  *stale* lock (owner dead) is reclaimed automatically, so this message means a real second loop.
- **Ticket ended `error` with no STATUS block** — read `logs/claude-loop/<run>/ticket-<n>.json`
  (`.result` field) and `.stderr`; usually a permission denial (extend `LOOP_ALLOWED_TOOLS`) or a
  mid-run crash.
- **Checks keep timing out** — raise `LOOP_CHECKS_TIMEOUT_MIN`; pr-verify runs the integration
  suite and can be slow on cold runners.
- **The picker returns nothing but issues exist** — they're excluded (labels/titles/deps/provenance);
  run `LOOP_SKIP_LABELS= LOOP_SKIP_TITLES= LOOP_SKIP_ISSUES= scripts/claude/next-ticket.sh` to see the
  unfiltered choice (its stderr names every ticket the provenance gate refused), then fix
  labels/deps on GitHub.
- **`REFUSED … has author_association …`** — the provenance gate did its job. Read the issue and its
  comments; then either run that ticket once with `LOOP_TRUST_GATE=0`, add the writer to
  `LOOP_TRUSTED_LOGINS`, or leave it for a human. `cannot read … (fail-closed)` instead means `gh`
  is unauthenticated or rate-limited — fix the API access, don't disable the gate.
