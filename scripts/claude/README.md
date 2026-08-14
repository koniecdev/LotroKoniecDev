# `scripts/claude/` — maintainer automation

**This directory is not for contributors.** Nothing here is needed to build, test, run or
contribute to LotroKoniecDev. It is the maintainer's autonomous backlog loop: it labels issues,
pushes branches, opens pull requests and squash-merges them into `main`, so it only works for
someone whose `gh` session already has write access to this repository. Running it from a fork
does nothing useful — it will fail at the first write.

If you are here to contribute, the things that *are* meant for you are:

- **[`CLAUDE.md`](../../CLAUDE.md)** — the project's conventions, layer rules and house style.
  Useful as a document even if you never touch an AI tool.
- **`.claude/commands/`** — `/spec`, `/feature`, `/ticket`, `/adr`, `/qa-ticket`. Ordinary
  workflow helpers; use them or ignore them.
- **`.claude/agents/dat-format-expert.md`** — the DAT binary format, VarLen encoding and the
  `datexport.dll` call surface, written down. Worth reading on its own.

## What each script does

| Script | Role |
|---|---|
| `backlog-loop.sh` | The conductor. Picks ready tickets and runs each in its own fresh headless process. Owns the merge gate. |
| `next-ticket.sh` | Deterministic ready-ticket picker — priority labels + `Depends on #X`. No LLM, no tokens. |
| `work-ticket.sh` | Runs exactly one ticket to completion in a fresh process, then judges its `STATUS: DONE\|BLOCKED` block. |
| `issue-trust.sh` | The provenance gate. See below — read this one before you touch anything. |

Full manual: **[`docs/claude-loop.md`](../../docs/claude-loop.md)**.

## Why `issue-trust.sh` exists, and why it stays on

This repository is public and its issues are open to anyone. The loop feeds an issue's **title,
body and comments** to an agent as instructions, and that agent can run `git`, `gh` and `dotnet`,
then auto-merge its own pull request once checks are green. Untrusted issue text is therefore a
prompt-injection channel that ends in `main`.

`issue-trust.sh` is what closes it (ADR-0026). It is **on by default** and **fails closed**: an API
error, a missing `author_association` or an unknown one all refuse the ticket. It checks every
comment author, not just the issue author, because "a later comment overrides the body" is part of
the worker's contract — gating only the author would leave the comment channel open on an otherwise
trusted ticket.

`LOOP_TRUST_GATE=0` disables it for one run. It exists for the case where the maintainer has
already read the issue *and its comments* personally. There is no other good reason to set it.

Publishing this file does not weaken the gate: it is an allowlist check, not a secret. Its
behaviour is covered by `scripts/tests/claude-loop-provenance.tests.sh`.
