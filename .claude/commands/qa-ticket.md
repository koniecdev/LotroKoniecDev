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
- `**blocked: <what is missing>**` — the precondition does not exist yet (no sample `exported.txt`,
  no seeded NeedsReview row, no admin login handed over). Say what is missing and who provides it.
  A ticket handed over with any blocked scenario also carries the **`qa-blocked`** label, so the gap
  sits in `gh issue list --label qa-blocked` instead of hiding inside an open ticket.

If more than half the ticket lands in owner-assisted/blocked, the ticket is not ready to hand over —
say so and fix the precondition first (`scripts/qa/seed-staging.sql` exists for the data half).

## 3. Prepend the read-first block, verbatim

Every QA ticket carries this at the very top, unchanged:

```markdown
> ### Read this before testing (applies to every QA ticket)
>
> **1. This app is Blazor Static SSR, not a SPA.** Pages are rendered on the server and arrive as
> finished HTML. There is no client-side state, no `fetch` from your browser to our API, and no
> service worker. Every API call happens server-to-server and is **invisible in the browser Network
> tab** — blocking or inspecting `/api/*` there shows nothing and proves nothing.
>
> **2. DevTools "Offline" does not simulate API downtime.** It cuts browser → frontend, so no page
> can be delivered at all — no server-rendered app can show a friendly banner when it cannot deliver
> any HTML. Scenarios marked _(owner-assisted)_ need the backend stopped server-side: **skip them**
> unless the owner runs them with you.
>
> **3. Do not improvise a missing precondition.** If a scenario needs data or a role you do not have
> (a superseded row, an admin login, a sample `exported.txt`), leave the box unchecked, write
> `blocked: <what is missing>` in your report, and **add the `qa-blocked` label to this ticket** so
> the owner sees it is waiting on them. A guessed substitute produces a false bug, which costs more
> than the untested scenario. A blocked scenario is the one case that keeps a finished run's ticket
> open — a scenario that simply failed is a result, so it does not block anything.
>
> **4. Ask before filing a security or "this looks wrong" bug.** One comment on this ticket is
> cheaper than a bug report that turns out to be by design. Vague auth messages and public download
> URLs are usually deliberate — ask which one it is.
>
> **5. If the app contradicts this ticket, the app is probably right.** These scenarios are written
> from the spec and can go stale. Report the contradiction as a question; do not assume it is a
> defect.
```

## 4. Ticket shape

Structure: read-first block → `## Context` → `### Environment & accounts (staging — browser only)`
→ `## Test scenarios` → `## Acceptance criteria`. Title convention `QA-FE-{nn}: Area — what is
covered`, labels `qa` + `type-test` + a priority, plus `qa-blocked` if any scenario ships blocked.

**Every step under `## Test scenarios` opens with its own id.** That id is what the run sheet's
`Test ID` column copies and what every later bug title cites:

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
   never reused. Ids leave the ticket — into the sheet, into bug titles, into other tickets — so
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
4. Add a dated `> **Corrected YYYY-MM-DD.** <what changed and why>` note so the tester can see the
   ticket moved under them, and cross-reference any bug report the old wording caused. If the
   ticket had already been run, say that its old numbers were positional, so anything filed
   against them can still be traced.
5. Keep the tester's already-checked boxes — do not silently reset their work.

## 7. Hand back

Print: the issue URL, the per-scenario classification counts (browser / owner-assisted / blocked),
and any precondition the owner must produce before the tester starts. Ask before creating or editing
the issue — it is outward-facing and a real person is about to act on it.
