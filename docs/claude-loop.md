# Claude backlog loop — this repository's part

The autonomous ticket loop runs every ticket in its own fresh headless `claude -p` process and its
own git worktree, and stops at the pull request (ADR-0060). It is maintainer-only: it needs write
access to this repository.

**The loop's code is not edited here.** `scripts/claude/` is a byte-for-byte copy of the
maintainer's shared loop engine, the same in every repository that runs the loop. The full manual,
with every outcome, setting and error message, is in the copy:

- [`scripts/claude/LOOP.md`](../scripts/claude/LOOP.md): the manual;
- [`scripts/claude/CONTRACT.md`](../scripts/claude/CONTRACT.md): what the engine reads from this
  repository (the settings, the prompts) and what this repository's prompts read from the engine;
- [`scripts/claude/README.md`](../scripts/claude/README.md): what each file does, and why the
  provenance gate exists.

This page holds only what this repository decides for itself.

## The normal flow

1. In an ordinary session, pick the tickets, for example every open `type-bug` + `area-auth`
   ticket that touches different files. `/preflight` helps.
2. Start the loop **in a plain terminal**, never from a Claude Code session (#969):
   `LOOP_TICKET_TIMEOUT_MIN=360 scripts/claude/start-loop.sh 812 830 842`, by its full path from
   another folder ("A longer ticket clock" below says why).
   `/backlog 812 830 842` prints that line for you. To let the running tickets finish and start
   nothing new, `touch .claude/backlog-loop.lock/stop-after-running` in the main checkout (#1050).
3. Read each PR. When you are happy with one, **assign yourself**: that is the approval, because
   GitHub does not let you approve your own PR (ADR-0060).
4. When the batch is read, run **`/merge-train`**. `/backlog` with no arguments reads the run's
   table back from the saved console.

The session's task is `.claude/commands/work-ticket.md`; an interactive run of one ticket is
`/ticket`, which uses the same worktree folder, so the loop skips a ticket an interactive run holds.

## This repository's settings

They live in [`scripts/claude-loop.conf`](../scripts/claude-loop.conf), and CI checks that they fit
the engine version of the copy. A one-run environment variable can override some of them
(`LOOP.md`, "Knobs").

| Setting | Value here | Why |
|---|---|---|
| `provenance_gate` | `on` | The repository is public, so issue text from anyone outside the writers never becomes a session's task (ADR-0026). |
| `trusted_associations` | `OWNER,MEMBER,COLLABORATOR` | The associations that carry write access (ADR-0026 §1). Widen it only with an ADR. |
| `trusted_logins` | empty | No second maintainer account or bot yet. |
| `require_private` | `off` | The repository is public; the gate above protects it instead. |
| `red_main_workflow` | empty | The worker's own gates stop on a red build; `ci.yml` going red on `main` does not stop a run. |
| `code_scanning` | `on` | CodeQL runs on every PR, and `/merge-train` refuses a PR with an open alert, so the table shows the alerts. |
| `e2e_image_label` | `lotrokoniecdev.e2e` | The E2E suites tag their images per worktree (`tests/Shared/E2EWorktree.cs`), so the worker removes them with the worktree. |
| `skip_labels` | `loop-blocked,epic,question,wontfix,invalid,duplicate,audit,post-mvp` | Not decided work, a tracking parent, an audit finding a human triages first (ADR-0026 §4; name the ticket to work it), and work cut from the MVP. |
| `skip_titles` | `^M4-` | The desktop app milestone (ADR-0033) targets the Windows patcher runtime, and its E2E criterion cannot run off Windows. |
| `skip_issues` | `85` | The forum watcher, cut to post-MVP (ADR-0030); work it only by naming it. |
| `gh_user` | `koniecdev` | The machine's active `gh` account may be one that cannot write here. |
| `claude_config_dir` | `.claude-account1` | The Claude account the sessions run on. |

## A longer ticket clock: 360 minutes (#1501)

The engine stops a ticket's run after `LOOP_TICKET_TIMEOUT_MIN` minutes (default 240, `LOOP.md` →
"Knobs"). A ticket stopped late has spent most of its cost and opens no PR, so its work is paid
for twice. Since the code review runs on Fable (#1318), a review round takes 15 to 30 minutes and
the gates about 10 more after each fix, so a ticket that opens a PR takes 1.5 to 3.5 hours on
Linux. So `/backlog` prints the launcher with `LOOP_TICKET_TIMEOUT_MIN=360`. It is an environment
variable of the engine, not a key of `scripts/claude-loop.conf`: when you start the loop by hand,
put it in front of the command yourself. The worker's start line in the console shows the clock a
run got (`timeout=360m`).

The worker must also know its clock. #1494 (and earlier #1376, #687 and #905) was stopped by the
engine and shows as `timeout`. #1488 ($79, three rounds and an APPROVE done, one second-pass fix
left) and #695 ($74) stopped themselves as `blocked` at 208 and 191 minutes, because their
240-minute clock was close. With 360, a session that still assumes 240 would stop far too early, so
`/work-ticket` reads its clock from its own environment, never from memory or a document, and a
session that stops itself for time ends `BLOCKED` with `CATEGORY: out-of-time`, not as a failed
review. The engine still labels it `loop-blocked` and asks for input, like any block: remove the
label to queue the ticket again. The prefix goes when the engine carries the value itself. Split a
ticket that would not fit, rather than raising the clock again.

## A new engine version

A change to the loop is made in the engine, never in `scripts/claude/`. It reaches this repository
in a pull request of its own, worked by hand with `/ticket`, never by the loop (the loop must not
rebuild itself):

1. In a fresh worktree of this repository, the engine's sync tool replaces `scripts/claude/` with
   one engine commit whose CI run ended green, and writes the stamp `scripts/claude-loop.engine`
   (that commit and the git tree id of the copy).
2. When the engine's contract version changed, the same PR updates `contract=` in the settings and
   the prompts that `CONTRACT.md` names (`work-ticket.md`, `ticket.md`, `backlog.md`).
3. The PR runs this repository's gates like any other.

Nothing here brings a new engine version by itself: the maintainer runs the sync.
`scripts/claude/verify-copy.sh --engine <engine checkout>` prints how many engine commits the copy
is behind. `pr-verify` runs `scripts/claude/verify-copy.sh` on every PR: it fails when the copy is not
exactly the tree its stamp names, or when the settings do not fit that engine version. It stops a
hand edit of the copy. It cannot stop an edit of the copy made together with the stamp's `tree=`
line, because it trusts the stamp: the PR's review does, as for any other change, and no session
ever edits the stamp by hand.
