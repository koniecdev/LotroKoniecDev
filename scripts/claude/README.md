# `scripts/claude/` — the maintainer's backlog loop

**This folder is not for contributors.** Nothing here is needed to build, test, run or contribute
to this repository. It is the maintainer's autonomous backlog loop: it labels issues, pushes
branches and opens pull requests (it never merges; the maintainer reviews and merges every PR), so
it only works for someone whose `gh` session already has write access to this repository. From a
fork it fails at the first write.

The folder is a copy of a shared engine, checked in CI by `verify-copy.sh`. Do not edit it here: a
change is made in the engine and copied over in a pull request of its own. What this repository
decides for itself is in `scripts/claude-loop.conf`; which engine version the copy is, is in
`scripts/claude-loop.engine`.

## What each file does

| File | Role |
|---|---|
| `start-loop.sh` | The one way to start the loop, in a plain terminal and never from a Claude Code session. Runs the conductor from its own checkout on `origin/main`. |
| `backlog-loop.sh` | The conductor. Runs the given (or ready) tickets, up to three at once, each in its own fresh headless process, and prints the PRs they opened. |
| `next-ticket.sh` | The picker: the next ready ticket by priority label, `Depends on #X` and "no open PR yet". No LLM, no tokens. |
| `work-ticket.sh` | Runs one ticket in its own worktree and a fresh process, then judges its `STATUS: DONE\|BLOCKED` block. |
| `issue-trust.sh` | The provenance gate, when the settings turn it on. See below. |
| `loop-config.sh` | Reads `scripts/claude-loop.conf`. |
| `verify-copy.sh`, `verify-copy.ps1` | Check that this folder is an untouched copy of one engine version and that the settings fit it. |
| `LOOP.md` | The full manual: every outcome and every setting. |
| `CONTRACT.md` | What the engine and this repository's prompts and settings read from each other. |

## Why `issue-trust.sh` exists

The loop feeds an issue's **title, body and comments** to an agent as instructions, and that agent
can run `git`, `gh` and the build, then push a branch and open a pull request under the
maintainer's account. In a public repository, untrusted issue text is therefore a prompt-injection
channel into a process with write access. The maintainer reads every PR before it merges, but that
review is the second line of defence, not the first.

`issue-trust.sh` closes it. With `provenance_gate=on` it runs before every session and **fails
closed**: an API error, a missing `author_association` or an unknown one all refuse the ticket. It
checks every comment author, not just the issue author, because a later comment overrides the body
in the worker's contract. With the gate off, the loop runs only in a repository that GitHub reports
as private.

`LOOP_TRUST_GATE=0` disables the gate for one run, for the case where the maintainer has already
read the issue *and its comments*. There is no other good reason to set it.

Publishing this file does not weaken the gate: it is an allowlist check, not a secret.
