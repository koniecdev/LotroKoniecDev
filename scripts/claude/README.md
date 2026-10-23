# `scripts/claude/` — maintainer automation

**This directory is not for contributors.** Nothing here is needed to build, test, run or
contribute to LotroKoniecDev. It is the maintainer's autonomous backlog loop: it labels issues,
pushes branches and opens pull requests (it never merges; the maintainer reviews and merges, see
ADR-0060), so it only works for someone whose `gh` session already has write access to this
repository. From a fork it fails at the first write.

If you are here to contribute, the things that *are* meant for you are:

- **[`CLAUDE.md`](../../CLAUDE.md)**: the project's conventions, layer rules and house style.
  Useful as a document even if you never touch an AI tool.
- **`.claude/commands/`**: `/spec`, `/feature`, `/ticket`, `/adr`, `/qa-ticket` and a few more.
  Ordinary workflow helpers; use them or ignore them.
- **`.claude/agents/dat-format-expert.md`**: the DAT binary format, VarLen encoding and the
  `datexport.dll` call surface, written down. Worth reading on its own.

## What each script does

| Script | Role |
|---|---|
| `start-loop.sh` | The one way to start the loop, in a plain terminal and never from a Claude Code session. Runs the conductor from its own checkout on `origin/main`. |
| `backlog-loop.sh` | The conductor. Runs the given (or ready) tickets, up to three at once, each in its own fresh headless process, and prints the PRs they opened. |
| `next-ticket.sh` | The picker: the next ready ticket by priority label, `Depends on #X` and "no open PR yet". No LLM, no tokens. |
| `work-ticket.sh` | Runs one ticket in its own worktree and a fresh process, then judges its `STATUS: DONE\|BLOCKED` block. |
| `issue-trust.sh` | The provenance gate. See below; read this one before you touch anything. |

Full manual, with every outcome and setting: **[`docs/claude-loop.md`](../../docs/claude-loop.md)**.

## Why `issue-trust.sh` exists, and why it stays on

This repository is public and its issues are open to anyone. The loop feeds an issue's **title,
body and comments** to an agent as instructions, and that agent can run `git`, `gh` and `dotnet`,
then push a branch and open a pull request under the maintainer's account. Untrusted issue text is
therefore a prompt-injection channel into a process with write access. The maintainer reads every
PR before it merges (ADR-0060), but that review is the second line of defence, not the first.

`issue-trust.sh` closes it (ADR-0026). It is **on by default** and **fails closed**: an API error,
a missing `author_association` or an unknown one all refuse the ticket. It checks every comment
author, not just the issue author, because a later comment overrides the body in the worker's
contract.

`LOOP_TRUST_GATE=0` disables it for one run, for the case where the maintainer has already read the
issue *and its comments*. There is no other good reason to set it.

Publishing this file does not weaken the gate: it is an allowlist check, not a secret.
`scripts/tests/claude-loop-provenance.tests.sh` covers it.
