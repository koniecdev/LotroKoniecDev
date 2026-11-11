# The contract between the engine and a repository that uses it

The engine is the set of files in this folder. A repository that uses it holds a byte-for-byte copy
of them in `scripts/claude/`, two files of its own next to that folder, and three prompts in
`.claude/commands/`. This file lists everything one side reads from the other.

A change to anything below changes the contract. Then the engine's `LOOP_CONTRACT` (in
`loop-config.sh`) goes up by one, and every repository updates its prompts and its `contract=`
line in the same pull request that brings the new engine version. Nothing checks that a changed
line here came with a new number: whoever changes the engine bumps it.

**Contract version: 1.**

## 1. Files a repository holds

| Path | Written by | What it is |
|---|---|---|
| `scripts/claude/` | the engine's `tools/sync.sh` | The copy of the engine at one engine commit. Never edited by hand. |
| `scripts/claude-loop.engine` | the engine's `tools/sync.sh` | The stamp: `commit=<engine commit>` and `tree=<git tree id of the engine folder at that commit>`. |
| `scripts/claude-loop.conf` | the repository | The settings (section 2). |
| `.claude/commands/work-ticket.md` | the repository | The session's task. Read by the session, never by the engine. |
| `.claude/commands/ticket.md` | the repository | The interactive ticket command. Its first step reads the loop's files (section 5). |
| `.claude/commands/backlog.md` | the repository | Prints the start command, and builds the roll-up from the loop's files and console (sections 5 and 6). |

The stamp and the settings sit outside `scripts/claude/`, so the copy stays byte for byte the
engine's folder and its tree id stays checkable.

### 1.1 The check of the copy

`scripts/claude/verify-copy.sh` (and its PowerShell twin `verify-copy.ps1`) passes only when:

- `git rev-parse <ref>:scripts/claude` equals the stamp's `tree=`. A tree id covers every file
  name, every byte and every exec bit, so an edit, an added file, a removed file or a lost exec bit
  all fail it. It reads committed files: the repository's CI runs it on the commit under test.
- `scripts/claude-loop.conf` at that commit holds exactly the keys this engine version knows, each
  once, with a value of the right shape, and `contract=` equals the engine's.

It does not compare `repo` with the checkout's origin, because a fork's clone has another origin;
the conductor and the worker do (section 2). With `--engine <engine checkout>` it also checks that
the stamp's commit exists there and has that tree, and prints how many engine commits the copy is
behind the engine's `origin/main`. A file and its stamp edited together pass without `--engine`.

The conductor refuses to start when `scripts/claude/`, the stamp or the settings differ from
`origin/main`, so the loop runs only reviewed code with reviewed settings.

## 2. Settings: `scripts/claude-loop.conf`

One `key=value` per line. A line that starts with `#` is a comment; blank lines are ignored; a
carriage return at the end of a line is dropped. A value is the rest of the line after the first
`=`, taken as it is: no quotes, no `$` expansion, no `~`. The file is read, never run. Every key
below is required, and a key the engine does not know is refused: a repository that misses a new
key finds out from its own CI, in the pull request that brings the new engine version, not when the
loop starts.

| Key | Values | What it does |
|---|---|---|
| `contract` | a number | Must equal the engine's contract version. |
| `repo` | `owner/name` | The GitHub repository of every `gh` call of the engine. The conductor, the worker, the picker and the gate refuse to run when it differs from the checkout's `remote.origin.url` (`https://github.com/<repo>(.git)`, `git@github.com:<repo>(.git)` or `ssh://git@github.com/<repo>(.git)`, compared without regard to case). |
| `gh_user` | a login, or empty | Whose `gh` token the run uses (`gh auth token --user`), unless the caller set `GH_TOKEN`. Empty: the active `gh` account. |
| `claude_config_dir` | a path; one that does not start with `/` is under `$HOME` | The Claude account the sessions run on (`CLAUDE_CONFIG_DIR`), unless the caller set `LOOP_CONFIG_DIR`. |
| `provenance_gate` | `on` / `off` | `on`: an issue that anyone outside the trusted writers wrote or commented on gets no session (`issue-trust.sh`). `off` is honoured only while GitHub reports the repository private; otherwise no session starts, whatever `require_private` says. |
| `trusted_associations` | comma list, or empty | The `author_association` values the gate trusts, e.g. `OWNER,MEMBER,COLLABORATOR`. With the gate on, this and `trusted_logins` may not both be empty. |
| `trusted_logins` | comma list, or empty | Extra logins the gate trusts. |
| `require_private` | `on` / `off` | `on`: no session starts unless GitHub reports the repository private, also with the gate on. |
| `red_main_workflow` | a workflow file name, or empty | No session starts while the last finished run of this workflow on `main` ended `failure` or `timed_out`. Empty: no such check. |
| `code_scanning` | `on` / `off` | `on`: the roll-up shows each PR's open code-scanning alerts. |
| `e2e_image_label` | a Docker label, or empty | When the worker removes a worktree, it also removes the Docker images with this label whose tag holds `-ticket-<n>-`. Empty: no Docker call. |
| `skip_labels` | comma list | The picker skips an issue with any of these labels. `loop-blocked` is skipped whatever this says. |
| `skip_titles` | a jq regex, or empty | The picker skips an issue whose title matches. |
| `skip_issues` | numbers separated by single spaces, or empty | The picker skips these issues. |

A one-run environment variable wins over its setting: `LOOP_GH_USER`, `LOOP_CONFIG_DIR`,
`LOOP_SKIP_LABELS`, `LOOP_SKIP_TITLES`, `LOOP_SKIP_ISSUES`, `LOOP_TRUSTED_ASSOCIATIONS`,
`LOOP_TRUSTED_LOGINS`. `LOOP_TRUST_GATE=0` skips the provenance gate for the run, in any
repository; it never skips the private-repository check.

## 3. What the engine passes to the session

The engine starts every session with the prompt `/work-ticket <n>` and nothing else. An account
hook may rely on that exact shape, so it does not change.

The session runs in its worktree, `<main>/.claude/worktrees/ticket-<n>`, with this environment:

- `CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1`: no background command; a command that reaches its
  timeout is stopped, not moved to the background;
- `BASH_MAX_TIMEOUT_MS` (default `3600000`) and `BASH_DEFAULT_TIMEOUT_MS` (default `600000`);
- `CLAUDE_CONFIG_DIR` from the settings or `LOOP_CONFIG_DIR`;
- `GH_TOKEN` of the settings' `gh_user`, when one was minted. The engine does not set `GH_REPO`
  for the session.

When the engine resumes a session, the prompt is one line of facts and nothing else:

    loop-resume reason=<reason> issue=<n> minutes_left=<m> bash_max_timeout_ms=<ms>[ pr_named=<k>]

| `reason` | When | How often |
|---|---|---|
| `no-status` | The session ended without a `STATUS:` line. | up to `LOOP_MAX_RESUMES` times (default 2) |
| `no-pr` | The session said `STATUS: DONE`, and the engine found no PR of the ticket (section 4). `pr_named` is the number of the PR the final message linked, when it linked one; a PR's branch name never reaches the prompt. | once |
| `limit` | A usage limit stopped the session, and the limit is over. The session runs in the worktree it left, uncommitted files included. | once per run |

What the session does for each reason is written in the repository's
`.claude/commands/work-ticket.md`, which the session read when it started, never in the engine. A
resume starts only with at least ten minutes left on the ticket's clock.

A session goes by the contract it started under, because the prompt it read stays in its
transcript even when it later rebases its worktree onto a newer `main`:

- A fresh session starts only when the fresh worktree's `scripts/claude-loop.conf` names the
  engine's contract: those are the prompts it will read. A worktree cut from a `main` that brought
  another contract gets no session (exit `3`).
- A resume in the same worker run always speaks the contract the session started under.
- A session kept after a usage limit records its contract in its marker (`contract=`). A later run
  resumes it only when that equals the engine's contract. A session kept under another contract,
  or by an engine older than contracts, is not resumed: the run treats it as a kept session that
  cannot be resumed (`LOOP.md`, "A kept worktree is resumed only when that is still safe"), which
  usually salvages its work and starts the ticket over.

## 4. What the engine reads from the session

- **The verdict**: a line of the final message that starts with `STATUS: BLOCKED` or
  `STATUS: DONE`. `BLOCKED` wins when both are there. A `BLOCKED` message is posted on the issue,
  cut to its first 4000 characters, under the line
  `🤖 **claude-loop: BLOCKED** — needs your input before the loop retries this ticket.`, and the
  issue gets the label `loop-blocked`.
- **The ticket's PR**, in this order (only the first `https://github.com/<owner>/<name>/pull/<k>`
  link of the final message is read, and every PR must be in `repo`, not from a fork, with a head
  branch that starts with `<n>-`):
  1. the linked PR, when it is open;
  2. an open PR of the ticket;
  3. the linked PR, when it was merged and created after the session started;
  4. a merged PR of the ticket created after the session started.
  A PR closed without a merge never counts.
- **A follow-up branch**: when a PR of the ticket was merged before the session's last fix was
  pushed, the session moves that fix to `<PR branch>-follow-up`, or `-follow-up-<k>`, cut from
  `main`, and opens a PR from it. The engine counts that branch as the session's own.

## 5. Files and names the engine writes in a repository

| Path or name | What |
|---|---|
| `<main>/.claude/worktrees/ticket-<n>` | The ticket's worktree, created detached from `origin/main`. Also the folder an interactive `/ticket <n>` uses. |
| `<worktree git folder>/loop-resume` | A session kept for a resume after a usage limit: `session=`, `head=`, `tree=`, `turns=`, `started=`, `contract=`. The loop resumes it; nobody else should start the ticket. |
| `<main>/logs/claude-loop/<YYYYmmdd-HHMMSS>/` | One conductor run. A worker started on its own writes to `adhoc-<YYYYmmdd-HHMMSS>/`. |
| `…/ticket-<n>.meta` | `key=value` lines; the last value of a key wins. Written before the worktree exists. Every way out of a worker writes `outcome=`. Keys: `issue`, `outcome`, `pr`, `resumes`, `worktree` (`kept`), `unmerged`, `salvage`, `started`, `branch`, `session`, `reset_at`, `minutes`, `turns`, `cost`. |
| `…/ticket-<n>.json` | The session's `claude -p --output-format json` result, with `total_cost_usd`. |
| `…/ticket-<n>.stderr` | The session's error output. |
| `<main>/logs/claude-loop/console-<YYYYmmdd-HHMMSS>.log` | The launcher's copy of the console. |
| `<main>/.claude/backlog-loop.lock/` | The run's lock: `pid` (the conductor's), and `stop-after-running` when the owner asks the run to start nothing new. |
| `<main>-loop/` | The launcher's own detached checkout on `origin/main`, from which the conductor runs. |
| `loop-salvage/<n>-<YYYYmmdd-HHMMSS>` | A branch with work a session left uncommitted, or committed on no branch. |
| `loop-blocked` | The label of a blocked ticket. |

`outcome` values: `pr-opened`, `merged`, `blocked`, `error`, `skipped`, `limit`, `timeout`,
`stopped`, `untrusted`, `no-worktree`.

The conductor's process has `backlog-loop.sh` in its command line. A worker's command line is
`bash <checkout>/scripts/claude/work-ticket.sh <n> <run folder>`.

## 6. Console lines

The conductor's lines start with `[conductor] `:

- `run <run folder> · up to <j> at once · tickets: <n> …` (or the picker's choice);
- `── start #<n> (<k>/<j> running) ──`;
- `stop requested — starting nothing new, waiting for #<n> …` (or `…, nothing is running`);
- `usage limit — starting nothing new until the running tickets end`, then
  `usage limit — sleeping until <HH:MM> (resets <HH:MM>, nap <k>/<max>)` or
  `usage limit — sleeping <m>m (nap <k>/<max>)`;
- `stopped early: <reason>`;
- the table: `ticket   outcome      PR     checks`, plus `   CodeQL alerts` with `code_scanning=on`,
  then one row per ticket: `#<n>`, the outcome (`running?` when the worker wrote none), `#<PR>`,
  the checks (`green`, `red`, `running`, `?` or `-`), the alerts with `code_scanning=on`, and the
  notes: `  resumed <k>x`, `  worktree kept: run #<n> again to resume its session`,
  `  <k> commit(s) not in the merged PR: check them`, `  could not check for commits after the merge`;
- `done: <a> PR opened · <b> merged · <c> blocked · <d> failed · <e> skipped · <f> untrusted · $<cost>`;
- `not started: #<n> … — start them later with: <launcher> -j <j> <n> …`;
- `next: review each PR, assign yourself to approve it, then run /merge-train`;
- `blocked tickets carry your questions as issue comments: gh issue list --label loop-blocked`;
- `raw session logs: <run folder>`.

A worker's lines start with `[loop] #<n> <HH:MM:SS> `. A worker that refuses a ticket says
`REFUSED` in that line.

## 7. Exit codes of `work-ticket.sh`

`0` PR opened · `2` blocked · `3` error (also: a repository that must be private and is not, or
whose visibility GitHub did not report; a red `main`; a gate that cannot read the issue; a fresh
worktree of another contract) · `4` timeout · `6` usage limit · `10` could not prepare the
worktree · `11` refused by the provenance gate · `12` skipped · `13` the PR was merged before the
session ended · `143` stopped by a signal.

## 8. What the picker reads from an issue

The labels (`priority-critical`, `priority-high`, `priority-medium`, `priority-low`, and the skip
labels), a title that starts with `[Epic]` or `[Tracking]`, and every `Depends on #<n>` (or
`depends-on #<n>`) line of the body, each of which must point at a closed issue.
