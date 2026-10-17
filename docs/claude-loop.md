# Claude backlog loop — the conductor manual

The autonomous ticket loop for this repo. **Loop control is deterministic bash; every ticket runs
in its own fresh headless `claude -p` process, in its own git worktree, and the process dies with
the ticket.** There is no long-lived orchestrator session, so nothing accumulates: per-ticket cost
is flat whether you run 1 ticket or grind the backlog all night.

**The loop stops at the pull request (ADR-0060).** It never merges. The normal flow is:

1. In an ordinary session, pick the tickets you want (for example every open `type-bug` +
   `area-auth` ticket that touches different files) — `/preflight` helps.
2. Start the loop **in a plain terminal**, never from a Claude Code session (see "Start it in a
   terminal" below): `scripts/claude/start-loop.sh 812 830 842`, by its full path when you are in
   another folder. `/backlog 812 830 842` prints that line for you. The loop runs up to three at
   once, each in `.claude/worktrees/ticket-<n>` cut from `origin/main`, and ends with a table of
   the PRs it opened; `/backlog` with no arguments reads that table back from the saved console.
3. Read each PR. When you are happy with one, **assign yourself** — that is the approval, because
   GitHub does not let you approve your own PR.
4. When the batch is read, run **`/merge-train`**. It merges only PRs you assigned yourself to, and
   only if nothing reached the PR after that, except a clean rebase onto `main` or the train's own
   conflict fix (ADR-0060, amended 2026-10-05). If something else did, read it, then unassign and
   assign again.

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
| `scripts/claude/start-loop.sh` | the terminal launcher — the one way to start a run: refuses while a loop runs, moves the loop's own checkout (`<main checkout>-loop`) to `origin/main`, hands over to the launcher copy there, runs the conductor under `caffeinate` and copies the console to `logs/claude-loop/console-<timestamp>.log` |
| `scripts/claude/backlog-loop.sh` | the conductor — up to `-j` tickets at once, lock, stop conditions, roll-up table of PRs |
| `scripts/claude/next-ticket.sh` | deterministic picker: priority labels + `Depends on #X` gate + skip rules + "no open PR yet" (a worktree kept for a resume after a usage limit does not hide its ticket) |
| `scripts/claude/issue-trust.sh` | the provenance gate: refuses an issue written by anyone without write access (ADR-0026) |
| `scripts/claude/work-ticket.sh` | one ticket: provenance gate → resume the session of a worktree kept after a usage limit, or skip if already in flight (its PR merged during the nap: done) → worktree from `origin/main` → fresh headless session → judge `STATUS:` (no line: resume the same session, at most `LOOP_MAX_RESUMES` times) → confirm the PR exists (merged while the session ran: done; DONE with no open PR: resume once) → remove the clean worktree (usage limit: keep it for the next run) |
| `.claude/commands/work-ticket.md` | the per-ticket discipline prompt (the old `ticket-worker` agent, promoted to a slash command) |
| `.claude/commands/backlog.md` | `/backlog <numbers>` prints the terminal command; `/backlog` with no arguments, after a run, reports the roll-up from the console copy and the run's `.meta` files. It never starts the loop |
| `~/.claude-account1/skills/merge-train/` | the maintainer's merge path — merges PRs the owner approved by assigning themselves (lives outside this repo) |
| `scripts/tests/claude-loop-{provenance,conductor,launcher}.tests.sh` | offline self-tests; all three run in `pr-verify` and `ci` |

## Usage

Start a run in a plain terminal, from any folder (by the script's full path when you are not in the
repository; a symlink to it, say in `~/.local/bin`, works too):

```bash
scripts/claude/start-loop.sh 123 130 131    # exactly these tickets, up to 3 at once (the normal use)
scripts/claude/start-loop.sh -j 1 123 130   # one at a time, in this order
scripts/claude/start-loop.sh -n 3           # the next 3 ready tickets from the picker
```

Its arguments, and every env var under "Knobs" below, go to the conductor. The pieces under it
still work on their own, in a checkout that is up to date with `main`:

```bash
scripts/claude/backlog-loop.sh              # the conductor without the launcher; no arguments = drain every ready ticket
scripts/claude/work-ticket.sh 123           # a single ticket, one fresh session, one worktree
scripts/claude/next-ticket.sh               # dry-run the picker (prints the next ready number)
```

**Why three at once, and not one.** Each ticket works in its own worktree, so tickets never share
files, and the token cost per ticket is the same either way — only the wall clock changes. What
the tickets do share is this machine: the CPU for builds and the Docker daemon for the test
containers. Three is the global cap on parallel sessions, and it is what the owner already runs by
hand. Two things keep parallel runs honest: tickets you pick should touch different areas (two
tickets that edit the same file give the second PR a conflict at merge time), and each E2E suite
must tag its Docker images per worktree, so two runs never test each other's build. That tagging
arrives with PR #895 (#884); until it is on `main`, run with `-j 1`.

### Start it in a terminal, never from a Claude Code session (#969)

Since Claude Code 2.1.285, a command that the Bash tool runs in the background is killed after two
hours at most, and a foreground call ends at its own timeout. The kill is a SIGKILL to the whole
process tree, so no trap runs: the conductor's lock stays, no worker salvages its work, no
`loop-resume` marker is written, and the next run skips each of those tickets because its worktree
still exists. A normal batch runs five hours or more. On 2026-09-29 a run of 15 tickets started from
a session died exactly two hours in, in the middle of three tickets; a run started the same way from
2.1.284 had gone on for 4.5 hours. A plain terminal has no such limit, so the loop starts there and
only there, and `/backlog` gives you the command instead of running it.

`start-loop.sh` does what you would otherwise do by hand before each run:

- It refuses inside a Claude Code session (the session sets `CLAUDECODE` in every command it runs;
  the conductor and the worker refuse it too), and it refuses arguments without ticket numbers and
  without `-n N` above zero, because the conductor would then take every ready ticket. It refuses
  while a loop runs (the lock's owner is alive and is `backlog-loop.sh`), and while a worker of the
  last run still salvages from the loop checkout. All of this comes before it touches anything.
- It runs the conductor from its own detached checkout next to the main one,
  `<main checkout>-loop`. It creates that checkout when it is missing (also when its folder was
  deleted by hand and git still lists it), refuses when it has local changes or is not a checkout
  of this repository, and moves it to `origin/main`. So the main
  checkout may sit on any branch, with any loop code in it, and you may keep working there. The
  logs, the lock and the ticket worktrees still live under the main checkout.
- It then hands over to its own copy in that checkout: whichever copy you start, the code that runs
  is the reviewed code on `origin/main`.
- It runs the conductor under `caffeinate -is` when `caffeinate` exists, because a Mac must not
  sleep in the middle of a run, and copies the console to `logs/claude-loop/console-<timestamp>.log`
  in the main checkout. The copy ignores Ctrl-C, a closed terminal and TERM (a logout sends TERM to
  every process), and ends only when every writer is done: if it ended first, a worker that writes
  its cleanup lines into the closed pipe would be killed by SIGPIPE before it salvaged anything.

Ctrl-C, or closing the terminal, stops the run as **stopped** below describes. `/backlog` with no
arguments then reads the newest console copy and the run's `ticket-<n>.meta` files, and writes the
roll-up. A worker killed with SIGKILL (a power cut, `kill -9`) still leaves its worktree without a
resume marker, and the loop skips that ticket until you clean the worktree up (see Troubleshooting).

**Cron (optional):** prefer a run you start yourself — cron on a sleeping laptop silently skips. If
the machine is awake at night anyway, add the entry below. Cron starts with a short `PATH`
(`/usr/bin:/bin`), so put a `PATH=` line with the folders of `gh`, `claude` and `jq` above it:

```
0 1 * * * ~/RiderProjects/LotroKoniecDev/scripts/claude/start-loop.sh -n 6 >> ~/RiderProjects/LotroKoniecDev/logs/claude-loop/cron.log 2>&1
```

## What "ready" means (the picker)

Open issue, not `[Epic]`/`[Tracking]`, none of the skip labels, **written only by trusted
maintainers** (see the provenance gate below), **no open PR yet** (a branch named `<n>-…` with an
open PR means the ticket waits for your review), **no `.claude/worktrees/ticket-<n>` yet** (a
session is on it) unless that worktree was kept for a resume after a usage limit — then the worker
decides, see "A session stopped by a usage limit, or DONE without a PR" — and every
`Depends on #X` in the body already
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
It runs again before each resume of a session (#925), because a resume is a new process that may
read the issue again.
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
| `LOOP_MAX_BUDGET_USD` | (none) | per-ticket API budget cap, off on purpose (owner decision, 2026-09-29, #953). The cap stops the session wherever it is. That can be just before the PR. A resume cannot help, because the cap counts the whole session. A nearly finished ticket that is thrown away costs more than the cap saves |
| `LOOP_PARALLEL` | `3` | tickets at once (same as `-j`) |
| `LOOP_ALLOW_LOCAL_SCRIPTS` | `0` | `1` = run even when `scripts/claude/` here differs from `origin/main` (only when you are changing the loop itself) |
| `LOOP_TICKET_TIMEOUT_MIN` | `240` | wall-clock kill switch per run of `work-ticket.sh`, its resumes included; the run that resumes a session after a usage limit starts a new clock, as the fresh retry did before; leftovers are committed on a `loop-salvage/…` branch. It is only a guard against a stuck session (#953). A normal ticket takes 50 to 80 minutes, and a session the clock kills is never resumed, so its ticket starts again from zero. A whole number of minutes above zero, without a leading zero; anything else is refused before the session starts |
| `LOOP_MAX_RESUMES` | `2` | how many times a session that ended normally without a `STATUS:` line is resumed before the ticket counts as `error`; `0` turns that resume off. The one resume of a DONE with no open PR and the resume after a usage limit do not count here |
| `BASH_MAX_TIMEOUT_MS` | `3600000` | the longest Bash timeout the worker may ask for (one hour), so the whole test suite fits in one foreground call — see "A session that stops without a verdict" |
| `BASH_DEFAULT_TIMEOUT_MS` | `600000` | the timeout of a worker Bash call that names none (ten minutes, not the CLI's two), because a call that runs out is stopped, not moved to the background |
| `LOOP_KEEP_WORKTREE` | `0` | `1` = keep `.claude/worktrees/ticket-<n>` after the run (by default a clean worktree is removed; the branch always stays). A worktree that would block a fresh start is removed anyway: one kept for a resume that can no longer happen, and one a usage limit left with no session to resume (#1096) |
| `LOOP_GH_USER` | `koniecdev` | gh account whose token backs the loop's gh write calls (labels, issue comments); an existing `GH_TOKEN` in the environment wins |
| `LOOP_SKIP_LABELS` | `loop-blocked,epic,qa,post-mvp,audit` | picker label exclusions |
| `LOOP_SKIP_TITLES` | `^M4-` | picker title-regex exclusion |
| `LOOP_SKIP_ISSUES` | `85` | picker number exclusions |
| `LOOP_TRUSTED_ASSOCIATIONS` | `OWNER,MEMBER,COLLABORATOR` | provenance gate: associations that carry write access |
| `LOOP_TRUSTED_LOGINS` | (none) | provenance gate: extra logins (a second maintainer account, a bot) |
| `LOOP_TRUST_GATE` | `1` | `0` = skip the provenance gate **for the whole run** (you read the issue *and* its comments yourself) — use it only on a single explicitly-named ticket |
| `LOOP_LIMIT_SLEEP_MIN` | `60` | the fixed nap after a usage limit, now only the fallback: the loop sleeps until two minutes after the time the limit message names (#1078), and takes this nap when no message names a time, or when those two minutes have already passed (the message cannot be read, or its time is older) |
| `LOOP_LIMIT_RETRIES` | `8` | max naps before giving up (a limit hit at the start of a 5h usage window needs up to ~5h of naps); a nap until the reset counts as one. A limit that ends later than `LOOP_LIMIT_RETRIES` × `LOOP_LIMIT_SLEEP_MIN` from now stops the run at once, with the reset time in the stop reason |
| `LOOP_MAX_CONSECUTIVE_FAILURES` | `2` | systemic-failure circuit breaker |

## Outcomes & triage

The loop prints one console line per outcome, then a table with one row per ticket (outcome, PR,
checks, open CodeQL alerts) and a totals line (counts + total cost). The table is your review
queue. Triage of blocked tickets happens on GitHub via the `loop-blocked` label. Raw per-ticket
session artifacts (`ticket-<n>.json` / `.stderr` / `.meta`) land in `logs/claude-loop/<timestamp>/`
for debugging only.

Per-ticket outcomes:

- **pr-opened** — the worker reported DONE and the PR exists. The loop does not wait for
  `pr-verify`: the table shows the checks as they stand at the end of the run, and `/merge-train`
  checks them again before any merge. A DONE with no open PR for the ticket is resumed once to
  open it (see "A session stopped by a usage limit, or DONE without a PR").
- **merged** — the worker reported DONE, and its PR was merged before its session ended (#1082). A
  session still waits for CodeQL after it opens its PR, and the PR looks finished in that time.
  Its body is final when it opens (#1083): the session edits it later only when something makes
  its report wrong, such as a fix commit. The work is on `main`, so the ticket is done:
  its session is not resumed, it does not count toward the failure circuit breaker, and its
  worktree is removed as for `pr-opened` (the branch stays). The table shows its PR, and the
  `done:` line counts it apart from `PR opened` and `failed`. Only a PR opened after the session
  started counts (on a resume after a usage limit: after the session first started). One merged
  before that is history from an earlier attempt, and a PR closed without a merge is still an
  `error`. An open PR of the ticket goes first, even when the summary links a merged one.
  - The session may still commit after the merge, for example a fix for a CodeQL alert, on the
    PR's branch, on a new branch or on a detached HEAD. That commit is not on `main`. So the worker
    compares both the worktree's HEAD and the PR's own branch with the merged PR's head
    (`refs/pull/<n>/head`), commit by commit and by change, not by id, because GitHub may rebase a
    PR before it merges it. A commit `origin/main` already has never counts, so a checkout of a
    newer `main` adds nothing. Files left uncommitted count as one more commit: the worker saves
    them on a `loop-salvage/…` branch. The row then says `<k> commit(s) not in the merged PR: check
    them`, and the worker's log line says where to look: the PR's branch, the worktree's HEAD and
    any salvage branch. Such commits usually need a new PR. The count can be too high, which is why
    the row asks you to check: a conflict fixed during a rebase, or an empty commit, counts too.
    When the merged head cannot be fetched, the row says `could not check for commits after the
    merge`. An OpenSSH remote gets that row too when a key needs a passphrase or a host key is new,
    also on a jump host. So does a remote that asks for a password. This fetch turns off the
    questions of git and OpenSSH, on the terminal and in askpass programs, so it fails instead of
    waiting (#1139). A jump host needs OpenSSH 8.4 or later for this. Either way the ticket stays
    `merged`: it is not a failure of the machine. `.meta` holds the count as `unmerged=` (`?` when
    it could not check).
  - Since #1097 the session checks the PR's state right before every push once the PR is open. On
    a merged PR it moves its new commits to a new branch from `main`, named after the PR's branch
    with `-follow-up` added, and opens a follow-up PR. The ticket then shows as `pr-opened` with
    that PR. The count above catches what still gets through: a merge in the seconds between the
    check and the push.
  - The loop cannot tell who merged the PR: the worker's `gh` runs under the owner's account. A
    session that merged its own PR would break ADR-0060, and it would show as `merged`, not as an
    `error`. The session's instructions forbid a merge, and that is the guard; this outcome does
    not police it.
  - A ticket is `merged` too when its PR is merged while a usage limit holds it (#1096): the
    session opened the PR, the limit stopped it, and you merged the PR during the nap. The next run
    of the ticket does not resume the session and does not start a new one. It counts the commits
    and files the merge did not take as above, saves uncommitted files on a `loop-salvage/…` branch,
    removes the worktree and keeps the branch. Only a PR from the branch the stopped session worked
    on counts, merged after that session first started. A PR merged from another branch of the
    ticket is someone else's work, so the ticket is `skipped`. See "A session stopped by a usage
    limit, or DONE without a PR" for each case.
- **skipped** — the ticket already has an open PR, or `.claude/worktrees/ticket-<n>` already
  exists (a manual `/ticket` session or an earlier run is on it). Nothing is started. A worktree
  kept for a resume after a usage limit is skipped too when resuming it is no longer safe, and so
  is a ticket stopped by a usage limit when a PR of it was merged from another branch during the
  nap (see "A session stopped by a usage limit, or DONE without a PR").
- **blocked** — the worker hit a genuine business question / dependency / mis-scope / red build.
  Since #1097 it also stops here when you closed its PR without a merge while the session still
  ran (`CATEGORY: pr-closed`), or when GitHub could not tell it the PR's state before a push
  (`CATEGORY: pr-state-unknown`); it pushes nothing more. The ticket gets the `loop-blocked` label
  and the exact questions as an issue comment. Triage: `gh issue list --label loop-blocked` →
  answer in a comment → remove the label → the loop can pick it up again.
- **failed / timeout** — session error or kill switch; leftovers are committed on a dedicated
  `loop-salvage/<n>-<timestamp>` branch (never stash — ordinary named git history). Two
  consecutive failures stop new starts (something systemic); the running tickets finish first.
  A session that ended without a `STATUS:` line is resumed first, and fails only after the cap
  (next section).
- **no-worktree** — `git fetch` or `git worktree add` failed. That is the machine, not the ticket,
  so the loop starts nothing more.
- **stopped** — you stopped the loop (Ctrl-C, `kill`, closing the terminal). Each running session
  gets 20 seconds to end the builds and tests it started (claude does that itself on TERM); then
  the loop ends whatever of them is left. Its leftovers are salvaged and its worktree is removed,
  so the next run can start the ticket again. A worker that has already seen its session end (it
  looks every 30 seconds), or that is already ending one because the clock ran out, does not stop
  half way: it finishes on its own and keeps its real outcome (a PR, BLOCKED, a worktree kept after
  a usage limit). The stop only cancels a resume that has not started yet. A stopped conductor
  prints no table, so `/backlog` builds the roll-up of such a run from its `.meta` files.
- **usage limit** — the loop starts nothing new, lets the running tickets finish, sleeps until
  the limit ends and runs the limited tickets again. Each one whose result names its session keeps
  its worktree, and the next run resumes that session there (see "A session stopped by a usage
  limit, or DONE without a PR"). When the limit outlasts every nap, the table row says
  `worktree kept: run #<n> again to resume its session`. How long it sleeps (#1078):
  - The limit message says when the limit ends: `You've hit your session limit · resets 8:20pm
    (Europe/Warsaw)`. The worker reads that time and writes it to `.meta` as `reset_at=`, and the
    conductor sleeps until two minutes after the latest `reset_at` of the limited tickets:
    `usage limit — sleeping until 20:22 (resets 20:20, nap 1/8)`, in the machine's time zone. With
    a fixed hour, the loop woke up to 38 minutes after the limit had ended, often after Claude had
    dropped the session's cached context (it keeps it for one hour after the last request). Or it
    woke before the end, met the limit again, and slept one more hour.
  - The message gives no day. The loop takes the first such clock time from one hour before the
    moment the worker saw the limit onward. Seen at 23:50, `resets 1:20am` is 1:20 the next
    day. Seen at 20:21, `resets 8:20pm` is 20:20 that day, already past: the limit has just ended,
    or the worker saw the session end a little late. Seen at 00:01, `resets 11:59pm` is the day
    before, so a limit that ended just before midnight is never read as tomorrow's.
  - No time, or the two minutes after it have already passed (the message has another shape, or
    its time is older): the fixed `LOOP_LIMIT_SLEEP_MIN` nap, as before. A wake-up time already
    past never gives a nap of zero, so a retry that meets the limit again cannot run through every
    nap in a few minutes. A limit that ended less than two minutes ago gets a short nap until those
    two minutes are over, and counts as one nap. Only the shape above is read,
    `resets <h>[:<mm>]am|pm (<IANA zone>)`, with a zone this machine knows. Anything else falls
    back, never a guess: a weekly limit with a date, a clock time that does not exist or exists
    twice on the night the clocks change, an earlier clock time seen in the hour after the clocks
    went back (the clock order cannot tell today from tomorrow then), or a zone that `date` would
    silently read as UTC.
  - A limit that ends later than the whole nap budget (`LOOP_LIMIT_RETRIES` naps of
    `LOOP_LIMIT_SLEEP_MIN`, eight hours by default) stops the run at once, with the reset time in
    the stop reason: `stopped early: usage limit resets at 2026-10-08 18:20, later than the whole
    nap budget (8 naps of 60m)`. The loop used to sleep the eight hours first and then stop anyway.
    A session limit ends at most five hours after it starts, so with the default knobs a session
    limit never stops the run this way: the stop needs a time more than eight hours away. A weekly
    limit whose message has a date is not read at all: it still takes the fixed naps until they
    run out.
- **untrusted** — the ticket failed the provenance gate, either at the start (no session is
  spawned) or before a resume (the work so far is kept on its branch). It does not count toward
  the failure circuit breaker (drain mode never selects one anyway).

### A session that stops without a verdict (#925)

On the night run of 2026-09-28, two of eight workers lost finished, reviewed tickets. Each one
started the whole test suite in the background and ended its turn to wait for it. A headless run
has no "later": `claude -p` ends a background shell about five seconds after its final message,
and nothing can wake the session again. Nothing was pushed, and the final message had no
`STATUS:` line, so the loop called it an error. Two things now stop that:

- **The worker's Bash tool cannot start a background run.** `work-ticket.sh` exports
  `CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1`. Checked in a real `claude -p` run: the Bash tool then
  has no `run_in_background` (a call with it fails validation), and a command that reaches its
  timeout is stopped instead of moved to the background. Without the switch it is moved, so even
  a foreground suite longer than the default ten-minute ceiling would end the same way. Per the
  CLI docs, subagents also run in the foreground with this switch. Because a long command is now
  stopped at its timeout, the script sets `BASH_MAX_TIMEOUT_MS` to one hour and
  `BASH_DEFAULT_TIMEOUT_MS` to ten minutes (unless you set them), and the worker prompt asks for a
  one-hour timeout on the suite. A shell-level `cmd &` can still escape the switch; the resume
  below covers that case too.
- **Resume instead of giving up.** If a session ends normally (no crash, no usage limit, no 429)
  and its final message has no `STATUS:` line, the script resumes the same session:
  `claude -p --resume <session_id>` with a short prompt. The prompt says that nothing runs in the
  background, asks the worker to run what it was waiting for in the foreground, finish the open
  steps and end with the STATUS block. The resume passes the same model, effort, permission and
  budget flags as the first run, works in the same worktree, and uses what is left of the
  ticket's wall clock. It starts only when at least ten minutes of that clock are left, and the
  prompt says how many remain. After `LOOP_MAX_RESUMES` resumes (default 2) the ticket is an
  `error` as before. A usage limit during a resume is still a usage limit (exit 6). `.meta` gets a
  new `resumes=` line as each resume starts (the last line wins, as for every key), and the
  end-of-run table marks such a ticket with `resumed Nx`.
- **The provenance gate runs again before each resume.** A resume is a new process that may read
  the issue again, and it can start hours after the first check. A comment from someone
  without write access that arrived in between refuses the ticket (exit 11, `untrusted`), exactly
  as it would at the start (ADR-0026).

A resume writes its result to `ticket-<n>.json.resume-<k>` and replaces `ticket-<n>.json` only
when it ends. The result it replaces moves to `ticket-<n>.json.before-resume-<k>`. Neither name
matches the conductor's `ticket-*.json`, and that matters for the cost total: a resumed run
reports the cost of the whole session, not of its own run. That was checked in a real run, and so
was the budget: `--max-budget-usd` also counts the whole session, so `LOOP_MAX_BUDGET_USD` stays
a cap per ticket. A resume that is killed by the clock or by you leaves the last finished result,
and the cost up to it, in `ticket-<n>.json`; only the killed run's own spend is missing, as it is
for a first run that is stopped. A
retry that starts over runs the ticket again in the same run folder, so it clears these files
first, just as it overwrites the `.json` and `.stderr` of the attempt before.

### A session stopped by a usage limit, or DONE without a PR (#934)

Two more endings used to throw away a session that was nearly done:

- **A usage limit.** The retry after the nap started the ticket again in a new session, from a new
  worktree. The commits carried over on the ticket branch, but everything the session had read,
  planned and reviewed was paid for a second time, and its uncommitted files ended up on a
  `loop-salvage/…` branch the new session never looked at. A limit that hits after the review,
  during the last test run, costs most of the ticket twice.
- **DONE, but no open PR.** A failed push or `gh pr create` made the ticket an `error`, and the
  reviewed work waited on a local branch until someone found it.

Now:

- **On a usage limit the worktree is kept exactly as the session left it**, uncommitted files
  included, and nothing is salvaged. A marker file in the worktree's own git folder
  (`.git/worktrees/<name>/loop-resume`) records the session id, the HEAD, a digest of every file
  git does not ignore (built in a copy of the index), the turns so far and when the session first
  started. It
  is not part of the tree, so `git status` stays as the session left it, and it goes away with the
  worktree. Re-creating the worktree from the ticket branch instead would lose the uncommitted
  files, and a session that had not cut its branch yet has no branch to re-create from.
- **The next run of the ticket resumes that session** in the kept worktree: `claude -p --resume
  <session_id>` with the same flags and a prompt that says the limit is over, time has passed (so
  check `git status` and whether the PR exists), and a command cut off by the limit must run
  again. The provenance gate runs first, as before every session. The run that resumes gets a new
  ticket clock (the nap is not work time), and the no-STATUS resume and its cap work in it as in
  any other run. The conductor needs no change for this: after the nap it runs the ticket again,
  and the worker finds the kept worktree. So does a later run where you name the ticket, and the
  picker returns such a ticket in drain mode too.
- **A kept worktree is resumed only when that is still safe.** The worker claims the marker with one
  rename, so two runs can never resume one session (the CLI would mix both into one transcript).
  When the session's transcript is not under `LOOP_CONFIG_DIR` (another account ran it, or the CLI's
  cleanup deleted it after its default 30 days), the session cannot be resumed: with an open PR for
  the ticket the run skips it and leaves the unfinished work in the worktree for you; without one it
  salvages and removes the worktree and starts fresh, unless a PR of the ticket was merged after the
  session started (see "A PR merged during the nap" below). It skips the ticket (exit 12), drops the
  marker and leaves the worktree for you when the HEAD or any file changed since the limit (someone
  works there), when a PR of the ticket was merged from another branch, or closed from this branch,
  after the session started, when one is open from another branch, or when the session's own open PR
  has commits its branch does not (a review fix or a `/merge-train` rebase during the nap: the
  session would build on a stale copy). A PR head the branch itself once had still counts as the
  session's own: it pushed, then rewrote the branch (a rebase before the force push), and nobody
  else's push enters that branch's reflog. An open PR from the kept branch is otherwise the
  session's own: the limit may have hit after `gh pr create`. A PR that ended before the session
  started is history (an older attempt from the same branch name) and does not count. A rebase that
  stopped half way detaches HEAD, so the kept branch is then read from the rebase's own record. When
  GitHub cannot list the PRs, or the kept files cannot be read, the run is an `error` and the marker
  stays for the next run. When the provenance gate refuses the ticket before the resume, the kept
  work is salvaged and the worktree removed, since that session will never run again.
- **A stop or a timeout during the resumed session** ends it like any other run: the work is
  salvaged, the worktree is removed, and the next run starts a fresh session.
- **Without a session id, a limit still starts over**, as before: the worktree is salvaged and
  removed (also with `LOOP_KEEP_WORKTREE=1`, or the retry would find it and skip the ticket), and
  the retry cuts a new one from `origin/main`. The limited run writes `started=`,
  `branch=` and `salvage=` to its `.meta`, because nothing else records them once the worktree is
  gone. The retry runs in the same run folder and reads them before it starts over.
- **A PR merged during the nap (#1096).** The session may have opened its PR before the limit hit,
  and you may merge that PR while the loop naps. Its work is on `main` then, so a second session
  would only redo it and maybe open a second PR. So before a resume, and before a retry that starts
  over, the worker looks for a PR of the ticket merged after the session first started:
  - **From the stopped session's branch** (the kept worktree's branch, or the `branch=` its `.meta`
    recorded), or from the branch that one follows up (the session's branch is the PR's branch with
    `-follow-up` added, #1097): the ticket is `merged` (exit 13) with that PR. Nothing is resumed or
    started. As for a PR merged before the session ended, the worker counts the commits and files
    the merge did not take: the kept worktree's HEAD, its uncommitted files and the PR's branch, or,
    when the worktree is already gone, the PR's branch and the `salvage=` branch. It saves
    uncommitted files on a `loop-salvage/…` branch, removes the worktree (unless
    `LOOP_KEEP_WORKTREE=1`) and keeps the branch. The log line names the PR and where to look.
  - **From another branch:** someone else finished the ticket, so it is `skipped` (exit 12) with
    no session, as for an open PR from another branch, and the row shows that PR. A kept worktree
    stays for you, without its marker. With no worktree left, the log line and `salvage=` name the
    branch that holds the stopped attempt's leftovers.
  - **An open PR from the kept branch goes first:** when the session opened a second PR from its
    branch after the first was merged (since #1097 from its follow-up branch), that one still
    waits for review, so the session is resumed to finish it, as at the end of a session (#1082).
  - A PR merged before the session first started is history from an earlier attempt and does not
    count. Another branch's PR, open or merged, goes before the session's own merged one: the
    ticket is `skipped`. The session's own merged PR goes before one closed from its branch, since
    the session may have closed a PR and opened a new one. A worktree with no marker is still skipped as "already exists", whatever
    was merged.
    When GitHub cannot list the merged PRs, the run is an `error` and a kept worktree keeps its
    marker for the next run.
  - A run in a new run folder (for example after the limit outlasted every nap and you name the
    ticket again) has no `.meta` from the limited run. With no kept worktree it cannot tell when
    the attempt started, so it starts a new session even when the PR was merged (#1138). Check the
    ticket before you name it again. The picker does not offer it in drain mode, because the merged PR
    closed the issue.
- **A DONE with no open PR for the ticket is resumed once**, with a prompt that says what the loop
  found and that it counts only an open PR whose branch starts with `<n>-`. When the PR the session
  opened was closed without a merge, and not by the session itself, the prompt tells it to push
  nothing, open no new PR and end BLOCKED (#1097). Otherwise it asks the session to push and open
  the PR. The prompt does not mention a merged PR, so it never points the session at a merge
  (ADR-0060). The prompt names a PR the summary linked by its number only: that PR's branch name
  is text anyone who opens a PR can choose, so it stays in the log (ADR-0026). The result is
  judged again from the top: `pr-opened` when the PR now exists, `merged` when it was merged in the
  meantime, `blocked` when the session answers BLOCKED, `error` when there is still none. The
  resume needs a real "no PR" from GitHub: when the ticket's PRs cannot be listed, the ticket is an
  `error` without it. A link to a wrong PR in the summary no longer fails a ticket whose own PR
  exists: the branch list finds that one. This resume has the same guards as the no-STATUS one (a
  session id, ten minutes of the clock, the provenance gate), but it does not count toward
  `LOOP_MAX_RESUMES`.

Files: the resumed run keeps the limited attempt's results for debugging. `ticket-<n>.json` is
replaced by the resume's result when it ends (the old one moves to `.before-resume-1`), because a
resumed run reports the cost of the whole session. The attempt's older `.before-resume-*`,
`.resume-*` and `.stderr` files get a `.limit-<time>` suffix. The stderr moves because its limit
message would make a later crash look like a usage limit. `.meta` gets `session=` whenever the run
ends on its own and its result names the session (a timeout or a stop leaves it only in
`ticket-<n>.json`), and `worktree=kept` on a limit that kept one; `started=` (when the session first
started, in seconds since 1970), `branch=` (the session's branch, empty when it had none) on every
limit, `reset_at=` (when the limit ends, in seconds since 1970) on a limit whose message names that
time, and `salvage=` when a limit with no session to resume saved leftovers on a salvage branch
(a retry that then ends `merged` or `skipped` without a worktree writes it again);
`unmerged=` on a `merged` ticket (see "Outcomes & triage"); `resumes=` counts every resume, the one
after the limit included, so the end-of-run table marks the ticket `resumed Nx`. When the resume
happens in a later conductor run, both runs' totals count the part of the session before the limit.

## Safety model

- **Only maintainer-written text becomes a task** — the provenance gate above, enforced in front of
  the session so no invocation path bypasses it. Self-tested by
  `scripts/tests/claude-loop-provenance.tests.sh`, which runs in `pr-verify` and `ci`.
- Each ticket gets its **own worktree** from `origin/main`; the main checkout is never touched,
  and the runner never deletes work — anything left behind is committed on a dedicated
  `loop-salvage/<n>-<timestamp>` branch, never stashed, never reset. So are commits a session made
  on no branch, because removing a worktree drops its reflog. A rebase or merge left half done is
  never committed over: that worktree stays exactly as it is, for you. So does a worktree kept for
  a resume after a usage limit, until its session finishes in it or you remove it. Only a clean
  worktree is removed, together with the E2E images tagged for it; the branch always stays.
- The loop runs only when `scripts/claude/` in the checkout that starts it matches `origin/main`.
  That guard exists only from ADR-0060 on: a branch cut before it still carries the old conductor,
  which merges PRs and checks out `main` in your main checkout. `start-loop.sh` meets it by itself:
  it runs the conductor from the loop checkout, which it has just moved to `origin/main`.
- The worker session may commit/push/PR (that authorization is the point of loop mode). **Nothing
  in the loop merges or assigns** (ADR-0060). The merge path is `/merge-train`, and it takes only
  PRs the owner assigned to themselves after the last push (a clean rebase and the train's own
  conflict fix do not count as a push), with green required checks and zero open CodeQL alerts —
  and it never deletes the branch.
- Stopping the conductor (Ctrl-C, `kill`, closing the terminal) stops every running worker at
  once, even in the middle of a usage-limit nap. Each worker then stops its session and
  every process group the session started: Claude Code runs each Bash command in a group of its
  own, so killing the session's own group would miss them. A group the session starts during the
  stop is ended too (#935): the worker pauses the session while it reads the groups, then reads
  them again every half second while the session shuts down. Only a group that the session starts
  and leaves behind in its very last half second can still escape. A watchdog does the same if the
  worker itself is SIGKILLed. It learns that the worker is gone from a pipe that only the worker
  holds open, not from a process number, so a number the system has already given to another
  program cannot keep it waiting (#997). The conductor signals only what bash's own job list still
  shows as running, so a worker that has already ended gets no signal, even when the system has
  given its process number to another program (#992). Only a worker that ends in the millisecond
  between that read and the signal can still get one. A second Ctrl-C does not cut that cleanup
  short, and neither does a stop that comes after a worker has seen its session end (see
  **stopped** above). `scripts/tests/claude-loop-conductor.tests.sh` pins the conductor
  side (with the slot count, the retry after a usage limit and the stop conditions), and
  `scripts/tests/claude-loop-provenance.tests.sh` the worker side.
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
- **Ticket ended `error` with no STATUS block** — the session was already resumed
  `LOOP_MAX_RESUMES` times, or it could not be resumed (no `session_id` in the result; `.meta`
  carries `session=` too). Read
  `logs/claude-loop/<run>/ticket-<n>.json` (`.result` field), the earlier results in
  `ticket-<n>.json.before-resume-*`, and `.stderr`; usually a permission denial (extend
  `LOOP_ALLOWED_TOOLS`) or a mid-run crash. The session id in the JSON still resumes by hand.
  Bring the worktree back, then resume the session inside it with the loop's account, switches
  and flags (the config dir must be `LOOP_CONFIG_DIR`, or the session is not found; without the
  permission flags every git/gh/dotnet call is refused):

  ```bash
  git worktree add .claude/worktrees/ticket-<n> <branch> && cd .claude/worktrees/ticket-<n>
  CLAUDE_CONFIG_DIR=~/.claude-account1 CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1 \
  BASH_MAX_TIMEOUT_MS=3600000 BASH_DEFAULT_TIMEOUT_MS=600000 \
  claude -p "…" --resume <session_id> --model <model> --effort <effort> --permission-mode auto \
    --allowedTools 'Bash(git:*)' 'Bash(gh:*)' 'Bash(dotnet:*)' 'Bash(scripts/:*)' 'Bash(./scripts/:*)'
  ```
- **"scripts/claude/ … differs from origin/main — refusing"** — the loop scripts run from the
  checkout you start them in, and that checkout is on an old branch or has local edits. Old loop
  code may still merge PRs (it did before ADR-0060). Start the loop with `start-loop.sh`, which
  runs it from a checkout on `origin/main`; set `LOOP_ALLOW_LOCAL_SCRIPTS=1` only when you are
  changing the loop itself and run `backlog-loop.sh` directly.
- **"start-loop: … has local changes, and the loop runs from there"** — someone edited files in the
  loop checkout (`<main checkout>-loop`). The launcher lists them and moves nothing. That checkout
  is the loop's alone: keep what you need, then `git -C <main checkout>-loop checkout -- .` or
  remove the untracked files.
- **"start-loop: … is not inside a checkout of the repository"** or **"… has no backlog-loop.sh"**
  — you started a *copy* of the launcher that lives outside the repository. Start the one in the
  repository, or make your wrapper a symlink to it (or a script that `exec`s it by its full path).
- **"start-loop: … is not a checkout of this repository"** — a folder that is not this
  repository's worktree sits where the loop checkout belongs. Move it away; the next start makes the
  checkout.
- **"start-loop: a loop is already running (pid N)"** — the same lock test as the conductor's: a
  live conductor owns the lock. Wait for it, or stop it with Ctrl-C in its terminal.
- **"… never from a Claude Code session (#969)"** (`start-loop.sh`, `backlog-loop.sh` and
  `work-ticket.sh` all check) — the script saw `CLAUDECODE`, which Claude Code
  sets in every command it runs. Run the command in a new terminal window, not in one that a Claude
  Code session started.
- **"start-loop: workers from … are still running"** — a worker still runs from the loop
  checkout, so the launcher does not move it, and it lists each one with its process number. After
  a normal stop they only salvage and end within seconds. If they go on, their conductor was killed
  with SIGKILL and they still work their tickets (up to `LOOP_TICKET_TIMEOUT_MIN`): wait for them,
  or stop each one with a plain `kill <pid>`, which lets it salvage first.
- **"start-loop: name the tickets, or -n N with N above zero"** — the arguments named no ticket and
  no count, and the conductor would have taken every ready ticket. To work through the whole
  backlog on purpose, give `-n` a number large enough.
- **A run started from a Claude Code session died at exactly two hours** — that is the Bash tool's
  background limit (see "Start it in a terminal"). Its tickets' worktrees have no resume marker,
  so the loop skips them: clean each one up as below, then start the run again with
  `start-loop.sh`.
- **A ticket is always "SKIPPED — … already exists"** — a worktree `.claude/worktrees/ticket-<n>`
  is still on disk: a manual session, or a run the loop could not clean up (a rebase or merge left
  half done, or leftovers it could not commit — the run's log line says which). Look inside, finish
  or abort what is there, then `git worktree remove .claude/worktrees/ticket-<n>`.
- **"SKIPPED — … was kept to resume …"** — the worktree waits for a resume after a usage limit, but
  resuming is no longer safe: its HEAD or its files changed, a PR of the ticket was merged or is
  open from another branch, or was closed from this branch, or the session's own PR has commits its
  branch does not. The line says which. A PR merged from the kept branch is not a skip: the ticket
  is `merged`. Decide what happens to the work in it, then remove the worktree (`--force` when it
  holds uncommitted files you no longer need). To give up a kept resume on purpose, just remove the
  worktree: the marker goes with it, and the next run starts a fresh session, which checks out the
  ticket branch.
- **The picker returns nothing but issues exist** — they're excluded (labels/titles/deps/provenance);
  run `LOOP_SKIP_LABELS= LOOP_SKIP_TITLES= LOOP_SKIP_ISSUES= scripts/claude/next-ticket.sh` to see the
  unfiltered choice (its stderr names every ticket the provenance gate refused), then fix
  labels/deps on GitHub.
- **`REFUSED … has author_association …`** — the provenance gate did its job. Read the issue and its
  comments; then either run that ticket once with `LOOP_TRUST_GATE=0`, add the writer to
  `LOOP_TRUSTED_LOGINS`, or leave it for a human. `cannot read … (fail-closed)` instead means `gh`
  is unauthenticated or rate-limited — fix the API access, don't disable the gate.
