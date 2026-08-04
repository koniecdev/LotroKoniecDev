---
description: Work a GitHub issue end-to-end — BRD/spec first, then branch, slim slice, tests, review, PR
argument-hint: <issue number> [extra context]
---

Work GitHub ticket **#$ARGUMENTS** end-to-end, BRD/spec-driven. Follow this loop — do not skip steps.

## 0. Preflight — sync `main` FIRST; fail loudly on a dirty tree

Run this before anything else, even before reading the ticket:

- `git status --porcelain` — **any output = STOP.** Report exactly what is uncommitted and end the
  turn; the user decides what happens to it. No stash, no auto-commit, no salvage branch — do not
  touch the tree.
- `git checkout main`
- `git pull --ff-only` — ff-only on purpose: local `main` must never carry its own commits, so a
  pull that can't fast-forward means something is wrong — fail loudly instead of silently
  rebasing stray commits into the next PR.

This makes the post-merge loop self-contained: after merging the previous PR on GitHub, `/clear` →
`/ticket <n>` needs no manual `git checkout main` + `git pull` first.

## 1. Pull the ticket

- `gh issue view <n> --comments` — read title (`M{milestone}-{nn}: Title`), labels, body
  (Context / Depends on / Tasks / Acceptance criteria) and every comment (later comments override
  the body).
- For each `Depends on #X` / `Blocks #X`: `gh issue view X` enough to know whether the dependency
  is satisfied. If an open dependency genuinely blocks this ticket, **stop and say so** — don't
  build on missing foundations.
- Issues created before the 2026-06 architecture pivot may describe a dead world (MediatR, one
  shared Application for all UIs, auth postponed to M5). If the body conflicts with `CLAUDE.md`,
  **CLAUDE.md wins** — surface the conflict and align the ticket before implementing.

## 2. Ground it in the repo

- Read the code areas the ticket touches; identify the **nearest sibling slice** to mirror.
- DAT/update-related? Check `docs/knowledge-base/` BEFORE planning — vnum semantics, translation
  survival, update detection and the launch flow are **already empirically settled** there.
- Skim `docs/adr/` for rulings that constrain the approach (ADR-0001: no mediator, slim handlers).

## 3. BRD/spec — decide the weight

- **Spec-worthy** (new feature, fuzzy rules, contract change, `feature` label on M-level scope):
  copy `docs/specs/_TEMPLATE.md` → `docs/specs/NNNN-kebab-title.md` (next free number). Fill
  Business context / Goal / scope / rules / Contract / Acceptance criteria from the ticket plus
  what the code implies — concrete types and paths, not placeholders. Refine the ticket's
  acceptance criteria into testable "done when…" items.
- **Open questions discipline:** questions answerable **empirically** → answer them from
  `docs/knowledge-base/` or the code and cite the source. Questions that are **business decisions**
  (behavior at boundaries, UX wording, scope cuts) → list them, **ask the user the top 3-5
  directly, and STOP**. Never invent answers. Fold the user's answers in, set
  **Status: Agreed**, only then continue.
- **Trivial** (crisp small bug/refactor with unambiguous AC): say a spec is overkill, write a
  3-line inline brief in your reply instead, and proceed.

## 4. Branch — always a fresh feature branch off main; never commit to main

Step 0 already left you on a clean, fresh `main`; anything dirty now is this session's own work
(e.g. the step-3 spec file), and an untracked file travels with the checkout:

- `gh issue develop <n> --base main --checkout` — creates + checks out the linked
  `{n}-{kebab-title}` branch off `main`. If it already exists, just check it out.
- **Never work on `main` directly.** If for any reason you find yourself past step 0 with commits
  to make and no ticket branch yet, cut the branch first — nothing is ever committed to `main`.

## 5. Implement

Follow the `/feature` discipline: mirror the sibling slice; slim SRP handler (record + handler +
validator for commands + DI registration + consumer wiring) — **no mediator**; an `/adr` first if
a non-trivial modeling decision emerges mid-flight. Honor every constraint the spec lists.

## 6. Verify "done"

- `dotnet build LotroKoniecDev.slnx` — green with **zero warnings** (TreatWarningsAsErrors).
- `dotnet test tests/LotroKoniecDev.Tests.Unit` — green; new behavior covered (happy path +
  failure modes + boundary `[Theory]` cases).
- Launch the **`code-reviewer`** agent with the ticket's acceptance criteria; fix what it finds.
  Re-run until clean.

## 7. Close the loop — commit logically, push, open the PR

- Mark spec **Status: Implemented** (if one exists).
- Summarize: files changed, acceptance criteria → how each is met, anything deferred.
- **Commit everything in logical commits** on the ticket branch (`git add -A` if needed) — group
  by concern, not one dump; leave nothing uncommitted.
- `git push -u origin HEAD`, then **open the PR** (`gh pr create --fill`) with the title mirroring
  the ticket title and a body containing `Closes #<n>` plus a short what/why/test summary. Mark it
  ready once the build + tests + `code-reviewer` gate from step 6 is green; leave it **draft** if
  anything there is still red — but always push, so the work is never lost.

If at any point the ticket turns out to be mis-scoped (wrong layer, contradicts an ADR or the
knowledge base), stop and report instead of forcing it — propose the correction as a comment
draft for the issue.
