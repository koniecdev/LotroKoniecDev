---
description: Loop-mode ticket worker — work ONE GitHub issue end-to-end in THIS (headless) session and end with the machine-readable STATUS block. Spawned per ticket by scripts/claude/work-ticket.sh; commit+push+PR are pre-authorized, merging is not.
argument-hint: <issue number>
---

> **Maintainer-only.** Spawned per ticket by `scripts/claude/work-ticket.sh` as part of the
> autonomous loop; it commits, pushes and opens a PR against this repository. Contributors working
> an issue by hand want `/ticket`, which asks before pushing.
> See [`scripts/claude/README.md`](../../scripts/claude/README.md).

Work GitHub ticket **#$ARGUMENTS** end-to-end in THIS session. You are the loop's per-ticket
worker: a fresh, isolated context that lives and dies with this one ticket. The conductor script
judges you ONLY by your final message — everything else you read or produce disappears with you.

**Loop-mode authorization:** entering this command IS the standing consent to branch → commit →
push → `gh pr create` (the interactive "ask before pushing" rule is waived). You never merge —
`gh pr merge` belongs to the conductor. You run unattended: nobody can answer questions mid-run.

## Prime directive — never invent answers

- A question that is **empirically answerable** (settled in `docs/knowledge-base/`, derivable from
  the code, a spec, or an ADR) — answer it yourself and cite the source.
- A question that is a **genuine business decision** (boundary behavior, UX wording, scope cut,
  contract shape not derivable from anything) — **STOP and return `STATUS: BLOCKED`** with the 3-5
  questions. A wrong guess merged into main costs far more than a paused ticket.

## The loop (do not skip steps)

1. **Pull the ticket.** `gh issue view $ARGUMENTS --json number,title,state,labels,body,comments`
   — one call returning title, labels, body (Context / Depends on / Tasks / Acceptance criteria)
   and the `comments` array (later comments override the body). **Never use `-c/--comments`** — it
   replaces the default view with a comments-only one, so a comment-free issue prints nothing and
   still exits 0. For each `Depends on #X`, verify it is satisfied; an open blocking dependency →
   `STATUS: BLOCKED` (category: dependency). Issues predating the 2026-06 pivot may describe a dead
   world (MediatR, one shared Application, auth in M5) — **CLAUDE.md wins**; note the conflict and
   build the current world.
2. **Ground it in the repo.** Read the areas the ticket touches; identify the nearest sibling
   slice to mirror (here, or TheKittySaver `AdoptionSystem.API/Features/…` + de-mediatorization).
   DAT/update work → `docs/knowledge-base/` FIRST (vnum, translation survival, launch flow are
   empirically settled — never re-test). Skim `docs/adr/` for constraints.
3. **Spec — decide the weight.** Spec-worthy (new feature, fuzzy rules, contract change) → copy
   `docs/specs/_TEMPLATE.md` → `docs/specs/NNNN-kebab-title.md`, fill it concretely, apply the
   Prime directive to every open question. Trivial (crisp bug/refactor) → 3-line inline brief in
   your summary and proceed.
4. **Branch.** `gh issue develop $ARGUMENTS --checkout` (off main; if it exists, check it out).
5. **Implement.** Mirror the sibling slice; honor every CLAUDE.md house rule (no mediator, slim
   SRP handlers, CQRS read/write split, ValueObjects over primitives, EF Fluent-only + `nameof()`
   columns, sealed types, explicit ctors, LINQ methods, zero warnings). A clear modeling decision
   emerging mid-flight → author an ADR in the house format; a genuinely contested one → `BLOCKED`.
6. **Verify "done".** `dotnet build LotroKoniecDev.slnx` — green, **zero warnings**. Run the unit
   tests for the touched area (+ the matching `.API.Tests.Integration` when the slice ships an
   endpoint) — green, with happy path + failure modes + boundary `[Theory]` cases. Then spawn the
   **`code-reviewer`** agent with the ticket's acceptance criteria; fix every finding; repeat until
   **APPROVE**. Run `/security-review` if the diff touches native interop, file protection, or
   auth. Cannot reach green/clean → `STATUS: BLOCKED` with the reason — never push broken work.
7. **Close out — git steps BEFORE the final message.** The review gate is a gate, not the finish
   line: after APPROVE, commit (message references the ticket, ends with the `Co-Authored-By:`
   footer), push, `gh pr create --fill --body "Closes #$ARGUMENTS"`. Never report DONE while work
   is only staged. Do NOT merge.
8. **CodeQL — clear every finding before you finish.** Wait for the PR's `CodeQL` check to
   complete (`gh pr checks <pr> --watch --fail-fast` or poll; docs-only diffs skip it), then list
   the PR's open alerts:
   `gh api "repos/{owner}/{repo}/code-scanning/alerts?ref=refs/pull/<pr>/merge&state=open"`.
   Fix every alert and push again (re-check after the re-run); dismissal instead of a fix is
   allowed only with a real stated reason. The conductor's merge gate refuses any PR with open
   alerts, so leaving one means the ticket ends in triage, not in a merge.

## Scope & safety

- **One ticket only.** Mis-scoped (wrong layer, contradicts an ADR or the knowledge base) → STOP,
  return `BLOCKED` with a proposed correction — don't force it.
- **Patcher is stable, not frozen:** any patcher change must keep every existing test green with
  assertions untouched, and must not regress behavior proven in `docs/knowledge-base/`.
- Reusable lesson learned the hard way this run → put it in the `LESSONS:` line of your final
  message (the flywheel); the user folds it into CLAUDE.md / agent memory / an ADR.

## Final message — the machine contract (nothing may follow it)

Your LAST message must be exactly one of these blocks — the runner greps `^STATUS:`. Review
output, test output, or a plan is NOT a valid ending.

```
STATUS: DONE
PR: <full PR url>
SUMMARY: <2-5 lines — what changed, how each acceptance criterion is met, review verdict>
LESSONS: <one line, or "none">
```

```
STATUS: BLOCKED
CATEGORY: business-questions | dependency | mis-scope | red-build | review-unclean
QUESTIONS:
- <the exact 3-5 questions or the specific blocker the user must resolve>
```
