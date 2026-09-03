---
description: Write or re-baseline a manual QA ticket for an external tester — every scenario verified against HEAD before handover
argument-hint: <area to cover> | #<issue> to re-baseline an existing one
---

Write (or refresh) a manual-QA ticket for an **external, browser-only tester**:

> $ARGUMENTS

The testers have no repo access, no Docker, no CLI and no way to tell whether this ticket is still
true. They execute what you write, literally, and they want to deliver — so a wrong line does not
get questioned, it gets reported as a bug.

**An unverified scenario is a defect.** It costs a false bug report, a triage session and the
tester's calibration. Measured on the first QA batch: of 8 tester bug reports only 2 were valid
findings, and the misses trace directly to ticket lines that were stale (#603), unexecutable on
staging (#547), contradicted by the code (#546), or invited browser-side fault injection
(#602, #604).

## 1. Verify every line — recall is not evidence

For **each** scenario before it may stay in the ticket, name the evidence in your working notes:

| Claim about… | Evidence you must have read |
|---|---|
| a route being public or gated | the endpoint's `.AllowAnonymous()` / `[Authorize]` / `@attribute [Authorize]` |
| an exact user-facing message | `grep` for the literal string in `src/` — quote what is really there |
| validation / rejection | the `IValidator<T>` rule or the domain guard that enforces it |
| conditional UI (a block, a button) | the `.razor` condition **and** what feeds it (a rel, a nullable field) |
| a status/state transition | the domain method that performs it, including the branch conditions |
| anything data-dependent | a live query against staging, e.g. `curl -s "https://tms.staging.lotro-translator.pl/api/v1/translations?status=NeedsReview&pageSize=1"` |

If you cannot cite it, the line does not ship. Rewrite it, mark it blocked, or drop it.

Watch specifically for the failure modes that already bit us:

- **The spec is not the code.** QA tickets get written from `docs/specs/` and then the product moves
  (#309 flipped the `polish.txt` download public a month after #271 said it was auth-gated). Read the
  code, not the spec, for every expectation.
- **A state the environment does not contain.** Staging holds ~792k rows but only a handful are
  Draft/Approved and often **zero** NeedsReview. Any scenario needing a non-default state must be
  checked against live data before it is handed over.
- **Fault injection a browser cannot perform.** This app renders server-side; "API down" cannot be
  simulated from DevTools.
- **A deliberate design that looks like a bug.** Public artifact download, generic auth errors,
  advisory-only warnings. Say in the ticket that it is deliberate, and why.

## 2. Classify every scenario, explicitly

- `[ ]` plain — the tester can do it alone in a browser.
- `_(owner-assisted — SKIP unless the owner runs it with you)_` — needs the backend stopped, a
  container killed, keys rotated, a broker outage, an admin-only import. Never phrase these as
  "optional": optional invites improvisation, and improvisation is what produced #602 and #604.
- `_(one-time — screenshot even on PASSED)_` — a step that changes or destroys state
  (an import, a deletion, a status transition): its "before" cannot be shown again
  tomorrow, so the wiki (§5) demands evidence even when it passes. The tag makes that visible in
  the ticket and machine-checkable by `/qa-run check`; the wiki default applies to untagged
  one-time steps too, so tag generously.
- `**blocked: <what is missing>**` — the precondition does not exist yet (no sample `exported.txt`,
  no seeded NeedsReview row, no admin login handed over). Say what is missing and who provides it.
  A ticket handed over with any blocked scenario also carries the **`qa-blocked`** label, so the gap
  sits in `gh issue list --label qa-blocked` instead of hiding inside an open ticket.

If more than half the ticket lands in owner-assisted/blocked, the ticket is not ready to hand over —
say so and fix the precondition first (`scripts/qa/seed-staging.sql` exists for the data half).

## 3. Prepend the pointer to the wiki — never the rules themselves

Every QA ticket opens with this one line, unchanged:

```markdown
> **Before you start:** paste the whole [tester workflow](https://github.com/koniecdev/LotroKoniecDev/wiki/Workflow-testera)
> into your assistant first. That page owns every rule — statuses, evidence, the report, when something
> is a bug. This ticket only says **what** to check.
```

**Do not paste a "Read this before testing" frame into the ticket.** Tickets used to carry a
~25-line copy of the wiki's pitfalls section, and the wiki removed it on 2026-08-26
(`Workflow-testera.md` §13): the frame was a copy of §10, so every rule change meant editing a
dozen tickets, and the tickets drifted from the page that outranks them. The rules live in exactly
one place; the ticket links to it.

Same rule for anything else the wiki already owns — statuses, the evidence standard, the report
format, when something counts as a bug. If you feel the urge to restate it "so the tester sees it
here", that is the drift the pointer exists to prevent. The ticket says **what** to check; the
wiki says **how** to test.

## 4. Ticket shape

Structure: the §3 pointer line → `## Context` → `### Environment & accounts (staging — browser only)`
→ `## Test scenarios` → `## Acceptance criteria` → `## Run report`. Title convention `QA-FE-{nn}: Area — what is
covered`, labels `qa` + `type-test` + a priority, plus `qa-blocked` if any scenario ships blocked.

**Every step under `## Test scenarios` opens with its own id.** That id is what every line of the
tester's run report (wiki §5) and every later bug title cites:

```markdown
## Test scenarios

### S01 — Cookie bar and the legal links   <!-- optional grouping — omit it on a short ticket -->

- [ ] **TC01** — Open a private window. Go to `https://staging.lotro-translator.pl`. …
- [ ] **TC02** — Scroll to the footer. The links "Regulamin" and "Polityka prywatności" …
- [ ] **TC03** — _(owner-assisted — SKIP unless the owner runs it)_ Stop the API, then …
```

Three rules, all owner decisions (2026-08-27, #742). The wiki (`Workflow-testera.md` §13) is
their source of truth — if it and this file ever disagree, the wiki wins:

1. **Ids are unique per ticket.** Numbering never restarts inside the next scenario.
2. **The id belongs to the step, not to its position.** Editing a ticket never renumbers it:
   a deleted step leaves a gap, a new step takes the next unused number, and a retired number is
   never reused. Ids leave the ticket — into the run report, into bug titles, into other tickets — so
   renumbering invalidates those citations silently and retroactively. A visible gap is harmless;
   a silently reused number is not.
3. **`### S01 — name` grouping is optional, and never enters the id.** It earns its place on a
   long ticket covering several areas; on a short single-flow ticket it is ceremony. The full id
   a bug title carries is always `QA-FE-{nn}-TC{kk}`.

**Do not mirror the existing QA-FE tickets for the step format.** 29 of the 30 written before
2026-08-27 are flat checkbox lists with no ids at all, and the one exception (#701) restarts TC
numbering inside every scenario, which rule 1 now forbids. Copy their tone and their level of
detail; take the shape from the block above.

Bug titles already filed against the old shape keep it — `BUG: QA-FE-24-S03-TC06 …` (#719) and
`BUG: QA-FE-27-S01-TC02 …` (#736) stay exactly as they are. Each still resolves against the ticket
it cites, and rewriting a citation is the precise failure rule 2 exists to prevent.

**Every ticket ends with `## Run report`** — the skeleton of the one comment per execution that
replaced the per-run sheet and the Drive folder (#767; wiki §5 owns the format and every rule
about it). One line of instruction, then a fenced block the tester copies with the code block's
copy button, posts as a comment when the run starts, and edits until it ends:

````markdown
## Run report

Copy the block below (copy button, top-right), post it as a new comment when you start, and edit
that comment as you go. The rules are in the wiki, §5.

```markdown
## Run — YYYY-MM-DD — @your-nick

**Environment:** https://staging.lotro-translator.pl — <browser / OS>
**Result:** 0 PASSED / 0 FAILED / 0 BLOCKED / 12 NOT RUN

- TC01 NOT RUN
- TC02 NOT RUN
- TC03 NOT RUN — owner-assisted
…

**Conclusion:**
```
````

One `- TCkk NOT RUN` per step, in document order, gaps kept; owner-assisted steps carry
`— owner-assisted` from the start; the `NOT RUN` count in `Result` is the step count. Nothing
else goes in — no status legend, no evidence rule, no column table: that is the wiki's, and §3
applies to this block as much as to the rest of the ticket. `/qa-run <n>` builds the same block
for a ticket that predates it, and `/qa-run <n> check` is the counterpart at the end of the run.

State the concrete environment (`https://staging.lotro-translator.pl`) and exactly which account
each leg needs. Where a scenario depends on a specific row, **name the FileId/GossipId** — do not
make the tester hunt for "a superseded row".

## 5. Batch discipline

Hand over **at most 3 tickets at a time**. A ticket verified today and executed in three weeks is a
stale ticket again; the 15-ticket batch is exactly how #271 rotted for a month. Verify → hand over
→ triage the results → verify the next three.

Every filed bug gets a verdict **the same day**: `valid` / `by design (+ why)` / `needs retest`.
Without that loop the tester keeps applying a wrong mental model, and every wrong report is one you
pay for twice.

## 6. Re-baseline mode (`#<issue>`)

When the argument is an issue number, do not rewrite the ticket — audit it:

1. Read the current body and run §1 against **every** line.
2. Fix only what is provably wrong; quote the code that proves it in the edit.
3. **Assign ids to steps that have none** — `TC01`, `TC02`, … following the order the steps are
   already in, so the numbering matches what a tester running the ticket today would have counted
   by position. Where the ticket already carries ids, **keep every one of them exactly as it is**:
   §4 rule 2 holds during a re-baseline too, so fixing a line never renumbers it, a step you drop
   leaves its number vacant, and a step you add takes the next unused one. A pre-rule ticket whose
   numbering restarts per scenario cannot satisfy rule 1 without a renumber; #701 is the only one,
   it is closed, and a closed QA ticket is replaced by a fresh run rather than re-baselined — so
   the case does not arise, and the answer is never to renumber a ticket someone has already run.
4. **Swap an old read-first frame for the §3 pointer** if the ticket still carries one. Tickets
   written before 2026-08-26 open with the ~25-line block the wiki has since dropped, and it is
   already stale in every one of them. Strip it **only here, on re-baseline** — do not go back and
   edit tickets nobody is about to re-run: a closed QA ticket is never resumed (a fresh run replaces
   it), so the edit would buy nothing.
5. Add a dated `> **Corrected YYYY-MM-DD.** <what changed and why>` note so the tester can see the
   ticket moved under them, and cross-reference any bug report the old wording caused. If the
   ticket had already been run, say that its old numbers were positional, so anything filed
   against them can still be traced.
6. Keep the tester's already-checked boxes — do not silently reset their work.
7. **Regenerate the `## Run report` block** from the ids the ticket carries after the edit, and
   append it if the ticket predates it. Ids never change here, so a run already posted under the
   ticket stays valid; only the skeleton for the *next* run moves.

## 7. Hand back

Print: the issue URL, the per-scenario classification counts (browser / owner-assisted / blocked),
and any precondition the owner must produce before the tester starts. Ask before creating or editing
the issue — it is outward-facing and a real person is about to act on it.
