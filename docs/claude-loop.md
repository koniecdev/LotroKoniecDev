# Claude backlog loop — the conductor manual

The autonomous ticket loop for this repo. Loop control is plain bash. Every ticket runs in its own
fresh headless `claude -p` process, in its own git worktree, and the process ends with the ticket.
No session lives across tickets, so the cost per ticket stays flat however many tickets run.

**The loop stops at the pull request (ADR-0060).** It never merges. The normal flow:

1. In an ordinary session, pick the tickets, for example every open `type-bug` + `area-auth`
   ticket that touches different files. `/preflight` helps.
2. Start the loop **in a plain terminal**, never from a Claude Code session (see "Start it in a
   terminal" below): `scripts/claude/start-loop.sh 812 830 842`, by its full path from another
   folder. `/backlog 812 830 842` prints that line for you. The loop runs up to three tickets at
   once, each in `.claude/worktrees/ticket-<n>` cut from `origin/main`, and ends with a table of
   the PRs it opened. `/backlog` with no arguments reads that table back from the saved console.
3. Read each PR. When you are happy with one, **assign yourself**. That is the approval, because
   GitHub does not let you approve your own PR.
4. When the batch is read, run **`/merge-train`**. It merges only PRs you assigned yourself to, and
   only if nothing reached the PR after that, except a clean rebase onto `main` or the train's own
   conflict fix (ADR-0060, amended 2026-10-05). If something else did, read it, then unassign and
   assign again.

## Why this shape (context economics)

A session that works many tickets re-reads all of them on every turn, so its cost per turn keeps
growing. That is why `/loop /ticket` and an in-session `/backlog` orchestrator with one subagent
per ticket were both retired. Here the orchestrator is not an LLM: a script picks tickets, starts
`claude -p "/work-ticket <n>"` for each one, reads the machine-readable result, and repeats. The
only LLM context is one per-ticket session.

The loop opens PRs and never merges them, because the owner reads every PR first. The one merge
path is `/merge-train` over PRs the owner approved (ADR-0060).

## Components

| Piece | Role |
|---|---|
| `scripts/claude/start-loop.sh` | the terminal launcher, the one way to start a run: refuses while a loop runs, moves the loop's own checkout (`<main checkout>-loop`) to `origin/main`, hands over to the launcher copy there, runs the conductor under `caffeinate` and copies the console to `logs/claude-loop/console-<timestamp>.log` |
| `scripts/claude/backlog-loop.sh` | the conductor: up to `-j` tickets at once, the lock, the stop conditions, the roll-up table of PRs |
| `scripts/claude/next-ticket.sh` | the picker: priority labels + `Depends on #X` gate + skip rules + "no open PR yet", no LLM |
| `scripts/claude/issue-trust.sh` | the provenance gate: refuses an issue written by anyone without write access (ADR-0026) |
| `scripts/claude/work-ticket.sh` | one ticket: provenance gate → worktree from `origin/main` (or the session of a worktree kept after a usage limit) → fresh headless session → judge `STATUS:` → confirm the PR exists → remove the clean worktree. Every ending is under "Outcomes & triage" |
| `.claude/commands/work-ticket.md` | the per-ticket prompt |
| `.claude/commands/backlog.md` | `/backlog <numbers>` prints the terminal command; `/backlog` with no arguments, after a run, reports the roll-up from the console copy and the run's `.meta` files. It never starts the loop |
| `~/.claude-account1/skills/merge-train/` | the maintainer's merge path: merges PRs the owner approved by assigning themselves (lives outside this repo) |
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

**Why three at once.** Each ticket works in its own worktree, so tickets never share files, and
the token cost per ticket is the same either way; only the wall clock changes. What they share is
the machine: the CPU for builds and the Docker daemon for the test containers. Three is the global
cap on parallel sessions. Pick tickets that touch different areas: two tickets that edit the same
file give the second PR a conflict at merge time. Each E2E suite tags its Docker images per
worktree (#884), so two runs never test each other's build.

### Start it in a terminal, never from a Claude Code session (#969)

Since Claude Code 2.1.285, a command the Bash tool runs in the background is killed after two hours
at most, and a foreground call ends at its own timeout. The kill is a SIGKILL to the whole process
tree, so no trap runs: the conductor's lock stays, no worker salvages its work, no `loop-resume`
marker is written, and the next run skips those tickets because their worktrees still exist. A
normal batch runs five hours or more (a 15-ticket run on 2026-09-29 died exactly two hours in). A
plain terminal has no such limit, so the loop starts there and only there, and `/backlog` gives you
the command instead of running it.

`start-loop.sh` does what you would otherwise do by hand before each run:

- It refuses inside a Claude Code session (the session sets `CLAUDECODE` in every command it runs;
  the conductor and the worker refuse it too). It refuses arguments with no ticket number and no
  `-n N` above zero, because the conductor would then take every ready ticket. It refuses while a
  loop runs (the lock's owner is alive and is `backlog-loop.sh`), and while a worker of the last
  run still salvages from the loop checkout. All of this comes before it touches anything.
- It runs the conductor from its own detached checkout next to the main one,
  `<main checkout>-loop`. It creates that checkout when it is missing (also when its folder was
  deleted by hand and git still lists it), refuses when it has local changes or is not a checkout
  of this repository, and moves it to `origin/main`. So the main checkout may sit on any branch,
  and you may keep working there. The logs, the lock and the ticket worktrees still live under the
  main checkout.
- It hands over to its own copy in that checkout, so whichever copy you start, the code that runs
  is the reviewed code on `origin/main`.
- It runs the conductor under `caffeinate -is` when `caffeinate` exists (a Mac must not sleep in a
  run), and copies the console to `logs/claude-loop/console-<timestamp>.log` in the main checkout.
  The copy ignores Ctrl-C, a closed terminal and TERM, and ends only when every writer is done, so
  a worker that writes its cleanup lines is never killed by SIGPIPE before it salvages.

Ctrl-C, or closing the terminal, stops the run as **stopped** below describes, and that also stops
the tickets in progress. To let them finish and start nothing new, ask for the stop with a file
instead (**stop requested** below): `touch .claude/backlog-loop.lock/stop-after-running` from the
main checkout. `/backlog` with no arguments then reads the newest console copy and the run's `ticket-<n>.meta` files, and writes the
roll-up. A worker killed with SIGKILL (a power cut, `kill -9`) still leaves its worktree without a
resume marker, and the loop skips that ticket until you clean the worktree up (see Troubleshooting).

**Cron (optional):** prefer a run you start yourself, because cron on a sleeping laptop silently
skips. If the machine is awake at night anyway, add the entry below. Cron starts with a short `PATH`
(`/usr/bin:/bin`), so put a `PATH=` line with the folders of `gh`, `claude` and `jq` above it:

```
0 1 * * * ~/RiderProjects/LotroKoniecDev/scripts/claude/start-loop.sh -n 6 >> ~/RiderProjects/LotroKoniecDev/logs/claude-loop/cron.log 2>&1
```

## What "ready" means (the picker)

A ticket is ready when it is open, carries none of the skip labels, has no `[Epic]`/`[Tracking]`
title, is **written only by trusted maintainers** (see the provenance gate below), and has:

- **no open PR yet**: a branch named `<n>-…` with an open PR means the ticket waits for your review;
- **no `.claude/worktrees/ticket-<n>` yet** (a session is on it), unless that worktree was kept for
  a resume after a usage limit; then the worker decides (see "A session stopped by a usage limit,
  or DONE without a PR");
- every `Depends on #X` in the body already **closed**. A ticket is closed by its merged PR, so
  closed means merged, and a ticket whose dependency still waits for review is not ready either.

Order: `priority-critical` > `priority-high` > `priority-medium` > `priority-low` > unlabelled,
then the lowest number first. Priority never outranks provenance. Label taxonomy:
`docs/labels.md`.

Default exclusions (each one can be changed with an env var):

- the labels `loop-blocked`, `epic` (a tracking parent has no work of its own), `question`,
  `wontfix`, `invalid`, `duplicate` (not decided work), `qa` and `qa-blocked` (manual passes),
  `audit` (a human triages audit findings; name one explicitly to work it) and `post-mvp` (cut from
  MVP). An epic is caught twice: by its `epic` label, and by the picker's own test for an
  `[Epic]`/`[Tracking]` title, which `LOOP_SKIP_TITLES` does not change;
- titles matching `^M4-` (the desktop app milestone, ADR-0033: it targets the Windows patcher
  runtime, and its E2E criterion cannot run off Windows);
- issue `#85` (M2-18 forum watcher, deferred post-MVP; work it only by naming it explicitly).

## The provenance gate (ADR-0026)

This repo is **public**: anyone can open an issue or comment on one, and `/work-ticket` reads the
title, body *and comments* as its instructions, then pushes a branch and opens a PR under the
owner's account. Your review before `/merge-train` is a second line of defence (ADR-0060), not a
reason to drop the first, so every path into the worker asks `issue-trust.sh` first:

> An issue is trusted only when its author **and every one of its commenters** has an
> `author_association` of `OWNER` / `MEMBER` / `COLLABORATOR` (write access), or a login listed in
> `LOOP_TRUSTED_LOGINS`.

It **fails closed**: a missing association or any GitHub API failure refuses the ticket. The gate
runs inside `work-ticket.sh`, not just in the picker, so `backlog-loop.sh 123` and a bare
`work-ticket.sh 123` are gated too. A refused ticket exits `11` and no session starts. The gate
runs again before each resume of a session (#925), because a resume is a new process that may read
the issue again. A gate that cannot reach the API is a fault of the machine, not of the ticket, so
it exits `3` (error) and the conductor's circuit breaker stops the run.

A stranger's harmless "+1" comment therefore parks a ticket: read it yourself, then run that one
ticket with `LOOP_TRUST_GATE=0`, or add the commenter to `LOOP_TRUSTED_LOGINS`.

## Knobs (env vars)

| Var | Default | Meaning |
|---|---|---|
| `LOOP_EFFORT` | worker effort from `~/.claude/model-policy.env`, else `high` | claude effort per ticket |
| `LOOP_MODEL` | worker model from `~/.claude/model-policy.env`, else `opus` | claude model per ticket |
| `LOOP_PERMISSION_MODE` | `auto` | headless permission mode |
| `LOOP_CONFIG_DIR` | `~/.claude-account1` | Claude config dir, so which account runs the loop (exported as `CLAUDE_CONFIG_DIR`) |
| `LOOP_ALLOWED_TOOLS` | git/gh/dotnet/scripts | loop-scoped Bash allowlist passed via `--allowedTools` |
| `LOOP_UNSAFE` | `0` | `1` = `--dangerously-skip-permissions` (full overnight autonomy) |
| `LOOP_MAX_BUDGET_USD` | (none) | per-ticket API budget cap, off on purpose (owner decision 2026-09-29, #953). The cap counts the whole session and stops it wherever it is, maybe just before the PR, and a resume cannot help. A nearly finished ticket thrown away costs more than the cap saves |
| `LOOP_PARALLEL` | `3` | tickets at once (same as `-j`) |
| `LOOP_ALLOW_LOCAL_SCRIPTS` | `0` | `1` = run even when `scripts/claude/` here differs from `origin/main` (only when you are changing the loop itself) |
| `LOOP_TICKET_TIMEOUT_MIN` | `240` | wall-clock kill switch per run of `work-ticket.sh`, its resumes included; the run that resumes after a usage limit starts a new clock. Leftovers go to a `loop-salvage/…` branch. It only guards against a stuck session (#953): a normal ticket takes 50 to 80 minutes, and a killed session is never resumed, so its ticket starts again from zero. A whole number above zero without a leading zero; anything else is refused before the session starts |
| `LOOP_MAX_RESUMES` | `2` | how many times a session that ended normally without a `STATUS:` line is resumed before the ticket counts as `error`; `0` turns that resume off. The resume of a DONE with no open PR and the resume after a usage limit do not count here |
| `BASH_MAX_TIMEOUT_MS` | `3600000` | the longest Bash timeout the worker may ask for (one hour), so the whole test suite fits in one foreground call |
| `BASH_DEFAULT_TIMEOUT_MS` | `600000` | the timeout of a worker Bash call that names none (ten minutes, not the CLI's two), because a call that runs out is stopped, not moved to the background |
| `LOOP_KEEP_WORKTREE` | `0` | `1` = keep `.claude/worktrees/ticket-<n>` after the run (by default a clean worktree is removed; the branch always stays). A worktree that would block a fresh start goes anyway: one kept for a resume that can no longer happen, and one a usage limit left with no session to resume (#1096) |
| `LOOP_GH_USER` | `koniecdev` | gh account whose token backs the loop's gh write calls (labels, issue comments); an existing `GH_TOKEN` wins |
| `LOOP_SKIP_LABELS` | `loop-blocked,epic,question,wontfix,invalid,duplicate,qa,qa-blocked,audit,post-mvp` | picker label exclusions |
| `LOOP_SKIP_TITLES` | `^M4-` | picker title-regex exclusion |
| `LOOP_SKIP_ISSUES` | `85` | picker number exclusions |
| `LOOP_TRUSTED_ASSOCIATIONS` | `OWNER,MEMBER,COLLABORATOR` | provenance gate: associations that carry write access |
| `LOOP_TRUSTED_LOGINS` | (none) | provenance gate: extra logins (a second maintainer account, a bot) |
| `LOOP_TRUST_GATE` | `1` | `0` = skip the provenance gate **for the whole run** (you read the issue *and* its comments yourself); use it only on a single explicitly named ticket |
| `LOOP_LIMIT_SLEEP_MIN` | `60` | the fallback nap after a usage limit, when no limit message names a reset time or that time is already past (#1078) |
| `LOOP_LIMIT_RETRIES` | `8` | max naps before giving up (a limit at the start of a 5h usage window needs up to ~5h of naps); a nap until the reset counts as one. A limit that ends later than `LOOP_LIMIT_RETRIES` × `LOOP_LIMIT_SLEEP_MIN` from now stops the run at once |
| `LOOP_MAX_CONSECUTIVE_FAILURES` | `2` | systemic-failure circuit breaker |

## Outcomes & triage

The loop prints one console line per outcome, then a table with one row per ticket (outcome, PR,
checks, open CodeQL alerts) and a totals line (counts and total cost). The table is your review
queue. Blocked tickets are triaged on GitHub with the `loop-blocked` label. Raw per-ticket session
files (`ticket-<n>.json` / `.stderr` / `.meta`) land in `logs/claude-loop/<timestamp>/`, for
debugging only.

Per-ticket outcomes:

- **pr-opened**: the worker reported DONE and the PR exists. The loop does not wait for
  `pr-verify`: the table shows the checks as they stand at the end of the run, and `/merge-train`
  checks them again before any merge. A DONE with no open PR is resumed once to open it (see "A
  session stopped by a usage limit, or DONE without a PR").
- **merged**: the worker reported DONE, and its PR was merged before the session ended (#1082). A
  session still waits for CodeQL after it opens the PR, whose body is final when it opens (#1083),
  so the PR looks finished in that time. The work is on `main`, so the ticket is done: no resume,
  no count toward the circuit breaker, and the worktree goes as for `pr-opened` (the branch stays).
  The `done:` line counts it apart from `PR opened` and `failed`. Only a PR opened after the
  session started counts (after a usage limit: after the session first started); an older merged
  PR is history from an earlier attempt. A PR closed without a merge is still an `error`, unless
  the resumed session answers BLOCKED (#1097). An open PR of the ticket goes first, even when the
  summary links a merged one.
  - **Commits after the merge.** The session may still commit after the merge (a CodeQL fix, say),
    and that commit is not on `main`. The worker compares the worktree's HEAD and the PR's branch
    with the merged head (`refs/pull/<n>/head`) by change, not by commit id, because GitHub may
    rebase a PR before it merges it. Commits `origin/main` already has never count; uncommitted
    files count as one more commit and are saved on a `loop-salvage/…` branch. The row then says
    `<k> commit(s) not in the merged PR: check them`, and the log line names where to look (the
    PR's branch, the worktree's HEAD, any salvage branch). Such commits usually need a new PR. The
    count can be too high (a conflict fixed in a rebase, an empty commit), which is why the row
    asks you to check. `.meta` holds it as `unmerged=`.
  - When the merged head cannot be fetched, the row says `could not check for commits after the
    merge` and `.meta` says `unmerged=?`. The fetch never asks a question: git and OpenSSH prompts
    are off, so a key with a passphrase, a new host key or a password request makes it fail
    (#1139; a jump host needs OpenSSH 8.4 or later for this). It also ends after 60 seconds,
    whatever it waits for (#1165). A stop during the fetch is only noted, so the worker ends at
    most about a minute later. The ticket stays `merged` either way.
  - Once the PR is open, the session checks its state right before every push (#1097). On a merged
    PR it moves its new commits to a new branch from `main` (the PR's branch name plus
    `-follow-up`) and opens a follow-up PR; the ticket then shows as `pr-opened` with that PR. The
    count above catches a merge that lands between the check and the push.
  - The loop cannot tell who merged the PR, because the worker's `gh` runs under the owner's
    account. A session that merged its own PR would break ADR-0060 and would still show as
    `merged`. The session's instructions forbid a merge, and that is the guard.
  - A ticket is `merged` too when you merged its PR while a usage limit held it (#1096). See "A PR
    merged during the nap" below.
- **skipped**: the ticket already has an open PR, or `.claude/worktrees/ticket-<n>` already exists
  (a manual `/ticket` session or an earlier run is on it). Nothing starts. A worktree kept for a
  resume is skipped too when resuming it is no longer safe, and so is a limited ticket whose PR
  was merged from another branch during the nap.
- **blocked**: the worker hit a real business question, a dependency, a mis-scope or a red build.
  It also stops here, pushing nothing more, when you closed its PR without a merge while the
  session ran (`CATEGORY: pr-closed`), or when GitHub could not give the PR's state before a push
  (`CATEGORY: pr-state-unknown`) (#1097). The ticket gets the `loop-blocked` label and the exact
  questions as an issue comment. Triage: `gh issue list --label loop-blocked` → answer in a comment
  → remove the label → the loop can pick it up again.
- **failed / timeout**: a session error or the kill switch. Leftovers are committed on a
  `loop-salvage/<n>-<timestamp>` branch (never stashed). Two failures in a row stop new starts
  (something systemic); the running tickets finish first. A session that ended without a `STATUS:`
  line is resumed first, and fails only after the cap (next section).
- **no-worktree**: `git fetch` or `git worktree add` failed. That is the machine, not the ticket,
  so the loop starts nothing more.
- **stopped**: you stopped the loop (Ctrl-C, `kill`, closing the terminal). Each running session
  gets 20 seconds to end the builds and tests it started (claude does that itself on TERM); then
  the loop ends whatever is left. Leftovers are salvaged and the worktree is removed, so the next
  run can start the ticket again. A worker that has already seen its session end (it looks every
  30 seconds), or is already ending one because the clock ran out, finishes on its own and keeps
  its real outcome (a PR, BLOCKED, a worktree kept after a usage limit); the stop only cancels a
  resume that has not started. A stopped conductor prints no table, so `/backlog` builds the
  roll-up from the `.meta` files.
- **stop requested**: you asked the loop to stop after the tickets that run now (#1050). A signal
  would stop those tickets too, so this is a file. From the main checkout:

  ```bash
  touch .claude/backlog-loop.lock/stop-after-running
  ```

  Before it starts a ticket, the conductor checks for the file. When it is there, the conductor
  starts nothing new and prints `stop requested — starting nothing new, waiting for #<n> …`. The
  running tickets go on as usual and end with their real outcome (a PR, BLOCKED, a worktree kept
  after a usage limit); nothing signals them. A usage-limit nap checks for the file once a minute
  and then ends, and the limited tickets are not run again: their worktrees stay, and the next run
  resumes their sessions. Then the conductor prints its table, `stopped early: stop requested — no
  new tickets`, and one line with every ticket it did not start (the limited ones first, then the
  rest of the list), ready to paste:
  `not started: #549 #816 — start them later with: <loop checkout>/scripts/claude/start-loop.sh -j 1 549 816`.
  The run exits `0`, as any run does unless no worktree could be made. When the picker chooses the
  tickets (no numbers given), the line lists only the limited tickets, because the picker chooses
  again next time. Any other early stop (two failures in a row, a usage limit that outlasts the
  naps) prints the same line.
  - The file lives in the lock folder, so it goes away with the lock when the run ends, and a lock
    left by a killed run is taken over with the file in it. So it can never stop the next run.
  - `touch` fails with `No such file or directory` when no loop runs, because the lock folder is
    not there.
  - Not "drain": in this repo, drain mode means a run of the picker over every ready ticket.
- **usage limit**: the loop starts nothing new, lets the running tickets finish, sleeps until the
  limit ends and runs the limited tickets again. Each one whose result names its session keeps its
  worktree, and the next run resumes that session there. When the limit outlasts every nap, the row
  says `worktree kept: run #<n> again to resume its session`. How long it sleeps (#1078):
  - The limit message names the end: `You've hit your session limit · resets 8:20pm
    (Europe/Warsaw)`. The worker writes that time to `.meta` as `reset_at=`, and the conductor
    sleeps until two minutes after the latest `reset_at` of the limited tickets, in the machine's
    time zone: `usage limit — sleeping until 20:22 (resets 20:20, nap 1/8)`. Waking on time
    matters, because Claude drops the session's cached context one hour after the last request.
  - The message gives no day. The loop takes the first such clock time from one hour before the
    moment the worker saw the limit onward: seen at 23:50, `resets 1:20am` is the next day; seen at
    20:21, `resets 8:20pm` is the same day and already past; seen at 00:01, `resets 11:59pm` is the
    day before.
  - With no time, or when the two minutes after it have already passed, it takes the fixed
    `LOOP_LIMIT_SLEEP_MIN` nap. A wake-up time already past never gives a nap of zero, so a retry
    that meets the limit again cannot burn every nap in minutes; a limit that ended less than two
    minutes ago gets a short nap and counts as one. Only `resets <h>[:<mm>]am|pm (<IANA zone>)`
    with a zone this machine knows is read. Everything else falls back, never a guess: a weekly
    limit with a date, a clock time that does not exist or exists twice on the night the clocks
    change, an earlier clock time seen in the hour after the clocks went back, or a zone `date`
    would silently read as UTC.
  - A limit that ends later than the whole nap budget (`LOOP_LIMIT_RETRIES` naps of
    `LOOP_LIMIT_SLEEP_MIN`, eight hours by default) stops the run at once, with the reset time in
    the stop reason: `stopped early: usage limit resets at 2026-10-08 18:20, later than the whole
    nap budget (8 naps of 60m)`. A session limit ends at most five hours after it starts, so with
    the default knobs it never stops the run this way. A weekly limit with a date is not read, so
    it takes the fixed naps until they run out.
  - The nap looks at the clock once a minute (#1199), because `sleep` does not count the time the
    computer sleeps (see `man 2 nanosleep`) and `caffeinate` cannot keep a closed laptop awake. So
    the nap ends at most a minute after its time, or after the computer wakes if that is later.
- **untrusted**: the ticket failed the provenance gate, at the start (no session starts) or before
  a resume (the work so far is kept on its branch). It does not count toward the circuit breaker.

### A session that stops without a verdict (#925)

A headless run has no "later": `claude -p` ends a background shell about five seconds after its
final message, and nothing wakes the session again. A worker that starts the test suite in the
background and ends its turn to wait loses its finished work (two tickets on 2026-09-28). Two
things prevent that:

- **The worker's Bash tool cannot start a background run.** `work-ticket.sh` exports
  `CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1`. The Bash tool then has no `run_in_background`, and a
  command that reaches its timeout is stopped instead of moved to the background; subagents run in
  the foreground too. So the script sets `BASH_MAX_TIMEOUT_MS` to one hour and
  `BASH_DEFAULT_TIMEOUT_MS` to ten minutes (unless you set them), and the worker prompt asks for a
  one-hour timeout on the suite. A shell-level `cmd &` can still escape the switch; the resume
  below covers that.
- **Resume instead of giving up.** If a session ends normally (no crash, no usage limit, no 429)
  and its final message has no `STATUS:` line, the script resumes it: `claude -p --resume
  <session_id>` with a short prompt that says nothing runs in the background and asks the worker
  to run what it waited for in the foreground, finish and end with the STATUS block. The resume
  uses the same flags and worktree and what is left of the ticket's clock. It starts only when at
  least ten minutes are left, and the prompt says how many. After `LOOP_MAX_RESUMES` resumes the
  ticket is an `error`. A usage limit during a resume is still a usage limit (exit 6). `.meta`
  gets a `resumes=` line as each resume starts (the last line wins, as for every key), and the
  table marks the ticket `resumed Nx`. The provenance gate runs again before each resume, and a
  comment from someone without write access refuses the ticket (exit 11, `untrusted`).

A resume writes its result to `ticket-<n>.json.resume-<k>` and replaces `ticket-<n>.json` only when
it ends; the result it replaces moves to `ticket-<n>.json.before-resume-<k>`. Neither name matches
the conductor's `ticket-*.json`, which matters for the cost total: a resumed run reports the cost of
the whole session. `--max-budget-usd` also counts the whole session, so `LOOP_MAX_BUDGET_USD` stays
a cap per ticket. A resume killed by the clock or by you leaves the last finished result in
`ticket-<n>.json`; only the killed run's own spend is missing. A retry that starts over clears these
files first in the same run folder.

### A session stopped by a usage limit, or DONE without a PR (#934)

Starting over after a usage limit would pay a second time for everything the session read, planned
and reviewed, and a DONE with no open PR (a failed push or `gh pr create`) would leave reviewed work
on a local branch. So:

- **On a usage limit the worktree is kept exactly as the session left it**, uncommitted files
  included, and nothing is salvaged. A marker in the worktree's own git folder
  (`.git/worktrees/<name>/loop-resume`) records the session id, the HEAD, a digest of every file
  git does not ignore, the turns so far and when the session first started. It is not part of the
  tree, so `git status` stays as the session left it, and it goes away with the worktree.
- **The next run of the ticket resumes that session** in the kept worktree: `claude -p --resume
  <session_id>` with the same flags and a prompt that says the limit is over, time has passed
  (check `git status` and whether the PR exists), and a command the limit cut off must run again.
  The provenance gate runs first. The run gets a new ticket clock, and the no-STATUS resume works
  in it as in any other run. After the nap the conductor runs the ticket again and the worker finds
  the kept worktree; so does a later run where you name the ticket, and the picker offers such a
  ticket in drain mode too.
- **A kept worktree is resumed only when that is still safe.** The worker claims the marker with
  one rename, so two runs never resume one session.
  - When the session's transcript is not under `LOOP_CONFIG_DIR` (another account ran it, or the
    CLI deleted it after its default 30 days), the session cannot be resumed. With an open PR for
    the ticket the run skips it and leaves the work in the worktree for you; without one it
    salvages, removes the worktree and starts fresh, unless a PR was merged during the nap (below).
  - It skips the ticket (exit 12), drops the marker and leaves the worktree for you when the HEAD
    or any file changed since the limit (someone works there); when a PR of the ticket was merged
    from another branch, or closed from this branch, after the session started; when one is open
    from another branch; or when the session's own open PR has commits its branch does not (a
    review fix or a `/merge-train` rebase during the nap). An open PR from the kept branch is
    otherwise the session's own, since the limit may have hit after `gh pr create`. A PR that
    ended before the session started is history.
  - When GitHub cannot list the PRs, or the kept files cannot be read, the run is an `error` and
    the marker stays for the next run. When the provenance gate refuses the ticket before the
    resume, the kept work is salvaged and the worktree removed.
- **A stop or a timeout during the resumed session** ends it like any other run: salvage, remove
  the worktree, and the next run starts fresh.
- **Without a session id, a limit still starts over:** the worktree is salvaged and removed (also
  with `LOOP_KEEP_WORKTREE=1`, or the retry would skip the ticket), and the retry cuts a new one
  from `origin/main`. The limited run writes `started=`, `branch=` and `salvage=` to its `.meta`,
  because nothing else records them once the worktree is gone; the retry reads them in the same
  run folder.
- **A PR merged during the nap (#1096).** You may merge the session's PR while the loop naps. A
  second session would only redo the work, so before a resume or a retry the worker looks for a PR
  of the ticket merged after the session first started:
  - **From the stopped session's branch** (the kept worktree's branch, or the `branch=` in
    `.meta`): the ticket is `merged` (exit 13), and nothing is resumed or started. The worker
    counts the commits and files the merge did not take (the kept HEAD, its uncommitted files and
    the PR's branch, or, with no worktree left, the PR's branch and the `salvage=` branch), saves
    uncommitted files on a `loop-salvage/…` branch, removes the worktree (unless
    `LOOP_KEEP_WORKTREE=1`) and keeps the branch.
  - **From the branch the session follows up** (its branch is the PR's branch plus `-follow-up`,
    #1097): a kept session is resumed to finish moving its fix. A retry with no session ends
    `merged` as above, and also counts the merged PR's commits the follow-up branch lacks. A branch
    that only starts with the PR's branch (`<branch>-follow-up-for-x`) is not a follow-up.
  - **From another branch:** someone else finished the ticket, so it is `skipped` (exit 12) and the
    row shows that PR. A kept worktree stays for you, without its marker; with none left, the log
    line and `salvage=` name the branch with the leftovers.
  - **An open PR from the kept branch goes first:** a second PR the session opened after the first
    merged still waits for review, so the session is resumed to finish it.
  - Order: another branch's PR, open or merged, goes before the session's own merged one (the
    ticket is `skipped`); the session's own merged PR goes before one closed from its branch. A
    worktree with no marker is still skipped as "already exists", whatever was merged. When GitHub
    cannot list the merged PRs, the run is an `error` and the marker stays.
  - A run in a new run folder (say, you name the ticket again after the limit outlasted every nap)
    has no `.meta` from the limited run. With no kept worktree it cannot tell when the attempt
    started, so it starts a new session even when the PR was merged (#1138). Check the ticket
    before you name it again. Drain mode does not offer it, because the merged PR closed the issue.
- **A DONE with no open PR for the ticket is resumed once.** The prompt says what the loop found
  and that only an open PR whose branch starts with `<n>-` counts. It first says that a session
  whose PR was closed without a merge, and not by the session itself, pushes nothing, opens no new
  PR and ends BLOCKED (#1097), since only the session knows who closed it. Otherwise it asks the
  session to push and open the PR. The prompt never mentions a merged PR, so it never points the
  session at a merge (ADR-0060), and it names a PR from the summary by number only, because a
  branch name is text anyone can choose (ADR-0026). The result is judged again: `pr-opened`,
  `merged`, `blocked` or, with still no PR, `error`. The resume needs a real "no PR" from GitHub;
  when the PRs cannot be listed, the ticket is an `error` without it. It has the same guards as the
  no-STATUS resume (a session id, ten minutes on the clock, the provenance gate) but does not count
  toward `LOOP_MAX_RESUMES`.

Files: the resumed run keeps the limited attempt's results. `ticket-<n>.json` is replaced by the
resume's result when it ends (the old one moves to `.before-resume-1`). The attempt's older
`.before-resume-*`, `.resume-*` and `.stderr` files get a `.limit-<time>` suffix; the stderr moves
because its limit message would make a later crash look like a usage limit. `.meta` keys:

- `session=` whenever the run ends on its own and its result names the session (a timeout or a stop
  leaves it only in `ticket-<n>.json`);
- `worktree=kept` on a limit that kept one;
- `started=` (when the session first started, seconds since 1970) and `branch=` (empty when it had
  none) on every limit;
- `reset_at=` (when the limit ends, seconds since 1970) when the message names that time;
- `salvage=` when a limit with no session to resume saved leftovers (written again by a retry that
  ends `merged` or `skipped` with no worktree);
- `unmerged=` on a `merged` ticket;
- `resumes=`, counting every resume, the one after the limit included.

When the resume happens in a later conductor run, both runs' totals count the part of the session
before the limit.

## Safety model

- **Only maintainer-written text becomes a task**: the provenance gate above, enforced in front of
  the session, so no path bypasses it.
- Each ticket gets its **own worktree** from `origin/main`; the main checkout is never touched, and
  the runner never deletes work. Anything left behind is committed on a
  `loop-salvage/<n>-<timestamp>` branch, never stashed, never reset, and so are commits a session
  made on no branch (removing a worktree drops its reflog). A rebase or merge left half done is
  never committed over: that worktree stays as it is, for you. So does a worktree kept for a resume,
  until its session finishes or you remove it. Only a clean worktree is removed, together with the
  E2E images tagged for it; the branch always stays.
- The loop runs only when `scripts/claude/` in the checkout that starts it matches `origin/main`,
  because a branch cut before ADR-0060 still carries the old conductor, which merges PRs.
  `start-loop.sh` meets this by itself: it runs the conductor from the loop checkout it has just
  moved to `origin/main`.
- The worker may commit, push and open a PR; that is the point of loop mode. **Nothing in the loop
  merges or assigns** (ADR-0060). `/merge-train` takes only PRs the owner assigned to themselves
  after the last push (a clean rebase and the train's own conflict fix do not count as a push),
  with green required checks and zero open CodeQL alerts, and it never deletes the branch.
- Stopping the conductor (Ctrl-C, `kill`, closing the terminal) stops every running worker at once,
  even during a usage-limit nap. Each worker stops its session and every process group the session
  started, since Claude Code runs each Bash command in a group of its own. A group the session
  starts during the stop is ended too (#935): the worker pauses the session while it reads the
  groups, then reads them again every half second while the session shuts down; only a group
  started in the very last half second can escape. A watchdog does the same if the worker itself is
  SIGKILLed, and it learns that from a pipe only the worker holds open, not from a process number
  (#997). The conductor signals only what bash's job list still shows as running, so a reused
  process number never gets a signal (#992). A second Ctrl-C does not cut the cleanup short.
  `scripts/tests/claude-loop-conductor.tests.sh` pins the conductor side (with the slot count, the
  retry after a usage limit and the stop conditions), `claude-loop-provenance.tests.sh` the worker
  side, and `claude-loop-launcher.tests.sh` the launcher.
- Business decisions are never invented: they come back as BLOCKED questions on the issue.
- The default permission mode is `auto` plus a loop-scoped git/gh/dotnet/scripts `--allowedTools`
  allowlist (interactive sessions are not affected). `LOOP_UNSAFE=1` trades that for full
  autonomy; your call per run.
- One loop at a time, via `.claude/backlog-loop.lock`. The lock records its owner PID, so the next
  conductor reclaims a crashed run's lock by itself. The stop request (`stop-after-running`) lives
  in the same folder and goes with it. A lock whose owner is alive is refused, and a
  refused start sends the same macOS notification a finished run does, so a scheduled loop never
  fails silently into a log file.

## Troubleshooting

- **"another loop is running (pid N)"**: a live conductor owns the lock; `ps -p N` shows it. A stale
  lock is reclaimed by itself, so this means a real second loop.
- **Ticket ended `error` with no STATUS block**: the session was already resumed
  `LOOP_MAX_RESUMES` times, or could not be resumed (no `session_id` in the result; `.meta` has
  `session=` too). Read `logs/claude-loop/<run>/ticket-<n>.json` (the `.result` field), the earlier
  results in `ticket-<n>.json.before-resume-*`, and `.stderr`. Usually it is a permission denial
  (extend `LOOP_ALLOWED_TOOLS`) or a crash. The session id still resumes by hand: bring the
  worktree back, then resume inside it with the loop's account, switches and flags (the config dir
  must be `LOOP_CONFIG_DIR` or the session is not found; without the permission flags every
  git/gh/dotnet call is refused):

  ```bash
  git worktree add .claude/worktrees/ticket-<n> <branch> && cd .claude/worktrees/ticket-<n>
  CLAUDE_CONFIG_DIR=~/.claude-account1 CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1 \
  BASH_MAX_TIMEOUT_MS=3600000 BASH_DEFAULT_TIMEOUT_MS=600000 \
  claude -p "…" --resume <session_id> --model <model> --effort <effort> --permission-mode auto \
    --allowedTools 'Bash(git:*)' 'Bash(gh:*)' 'Bash(dotnet:*)' 'Bash(scripts/:*)' 'Bash(./scripts/:*)'
  ```
- **"scripts/claude/ … differs from origin/main — refusing"**: the checkout you started from is on
  an old branch or has local edits, and old loop code may still merge PRs. Start the loop with
  `start-loop.sh`; set `LOOP_ALLOW_LOCAL_SCRIPTS=1` only when you are changing the loop itself and
  run `backlog-loop.sh` directly.
- **"start-loop: … has local changes, and the loop runs from there"**: someone edited files in the
  loop checkout (`<main checkout>-loop`). The launcher lists them and moves nothing. That checkout
  is the loop's alone: keep what you need, then `git -C <main checkout>-loop checkout -- .` or
  remove the untracked files.
- **"start-loop: … is not inside a checkout of the repository"** or **"… has no backlog-loop.sh"**:
  you started a copy of the launcher outside the repository. Start the one in the repository, or
  make your wrapper a symlink to it (or a script that `exec`s it by its full path).
- **"start-loop: … is not a checkout of this repository"**: a folder that is not this repository's
  worktree sits where the loop checkout belongs. Move it away; the next start makes the checkout.
- **"start-loop: a loop is already running (pid N)"**: the same lock test as the conductor's. Wait
  for it, or stop it with Ctrl-C in its terminal.
- **"… never from a Claude Code session (#969)"** (`start-loop.sh`, `backlog-loop.sh` and
  `work-ticket.sh` all check): the script saw `CLAUDECODE`. Run the command in a new terminal
  window, not in one a Claude Code session started.
- **"start-loop: workers from … are still running"**: a worker still runs from the loop checkout,
  so the launcher does not move it, and it lists each one with its process number. After a normal
  stop they only salvage and end within seconds. If they go on, their conductor was SIGKILLed and
  they still work their tickets (up to `LOOP_TICKET_TIMEOUT_MIN`): wait, or stop each one with a
  plain `kill <pid>`, which lets it salvage first.
- **"start-loop: name the tickets, or -n N with N above zero"**: the arguments named no ticket and
  no count. To work the whole backlog on purpose, give `-n` a large enough number.
- **A run started from a Claude Code session died at exactly two hours**: the Bash tool's limit
  (see "Start it in a terminal"). Its worktrees have no resume marker, so the loop skips them:
  clean each one up as below, then start again with `start-loop.sh`.
- **A ticket is always "SKIPPED — … already exists"**: `.claude/worktrees/ticket-<n>` is still on
  disk: a manual session, or a run the loop could not clean up (a rebase or merge left half done,
  or leftovers it could not commit; the run's log line says which). Look inside, finish or abort
  what is there, then `git worktree remove .claude/worktrees/ticket-<n>`.
- **"SKIPPED — … was kept to resume …"**: resuming is no longer safe (its HEAD or files changed, a
  PR of the ticket was merged or is open from another branch, or was closed from this branch, or
  the session's own PR has commits its branch does not); the line says which. Decide what happens
  to the work, then remove the worktree (`--force` when it holds uncommitted files you no longer
  need). To give up a kept resume on purpose, just remove the worktree: the marker goes with it,
  and the next run starts a fresh session on the ticket branch.
- **The picker returns nothing but issues exist**: they are excluded (labels, titles, dependencies,
  provenance). Run `LOOP_SKIP_LABELS=none LOOP_SKIP_TITLES='^$' LOOP_SKIP_ISSUES= scripts/claude/next-ticket.sh`
  to see the unfiltered choice (its stderr names every ticket the gate refused), then fix the
  labels or dependencies on GitHub. An empty `LOOP_SKIP_LABELS` or `LOOP_SKIP_TITLES` means the
  default, not "no filter".
- **`REFUSED … has author_association …`**: the provenance gate did its job. Read the issue and its
  comments, then run that ticket once with `LOOP_TRUST_GATE=0`, add the writer to
  `LOOP_TRUSTED_LOGINS`, or leave it for a human. `cannot read … (fail-closed)` means `gh` is not
  authenticated or is rate-limited: fix the API access, do not disable the gate.
