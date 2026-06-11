---
description: Work a GitHub issue end-to-end — BRD/spec first, then branch, slim slice, tests, review, PR
argument-hint: <issue number> [extra context]
---

Work GitHub ticket **#$ARGUMENTS** end-to-end, BRD/spec-driven. Follow this loop — do not skip steps.

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

## 4. Branch

`gh issue develop <n> --checkout` — creates the linked `{n}-{kebab-title}` branch off main.
(If it exists, just check it out.)

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

## 7. Close the loop

- Mark spec **Status: Implemented** (if one exists).
- Summarize: files changed, acceptance criteria → how each is met, anything deferred.
- Offer the PR: `gh pr create` with the title mirroring the ticket title and body containing
  `Closes #<n>` plus a short what/why/test summary. **Ask before pushing or creating the PR** —
  never push unprompted.

If at any point the ticket turns out to be mis-scoped (wrong layer, contradicts an ADR or the
knowledge base), stop and report instead of forcing it — propose the correction as a comment
draft for the issue.
