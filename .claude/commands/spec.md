---
description: Turn a rough feature idea into an agreed spec (seed → questions → spec) BEFORE any code
argument-hint: <one-line feature idea>
---

Produce a written spec for this idea **before writing any code**:

> $ARGUMENTS

You are running the seed → questions → spec loop from `CLAUDE.md`. **Do not implement anything.**

## 1. Ground it

- Read the domain models and the nearest existing `Application/Features/<Area>/` slices this
  touches. The spec must respect existing ADR rulings (`docs/adr/` — especially ADR-0001: slim
  handlers, no mediator) — if the idea contradicts one, say so explicitly.
- DAT/update-related ideas: consult `docs/knowledge-base/` first — update behavior, vnum
  semantics, translation survival and the launch flow are empirically settled; don't re-open them.
- Copy `docs/specs/_TEMPLATE.md` to `docs/specs/NNNN-kebab-title.md` (next free number, mirroring
  the ADR numbering).

## 2. Draft the seed

Fill **Business context / Goal / In scope / Out of scope / Contract / Acceptance criteria** from
the idea plus what the code already implies. Be concrete: real record/handler/service names, real
CLI verbs and exit codes, real file paths — not placeholders.

## 3. Surface the gaps — this is the whole point

In **Open questions**, separate two kinds:

- **Empirical questions** (how does the DAT/launcher/forum behave?) — answer them yourself from
  `docs/knowledge-base/` or the code, citing the source. Only a genuinely untested behavior may
  stay open, marked `[needs live test]`.
- **Business decisions only the user can make** — scope cuts, behavior at the boundaries (file
  missing, malformed line, game running, concurrent patch), UX wording, defaults. **Extract
  these — never invent the answers.**

## 4. Stop and hand back

Leave **Status: Draft**, print the file path, and ask the user the top 3-5 open questions directly
in your reply. When they answer, fold the answers in, flip to **Status: Agreed** — and only then
is it ready for `/feature`.

If the user wants the work tracked, offer to create the GitHub issue from the agreed spec:
`gh issue create` with the `M{milestone}-{nn}: Title` convention, labels, and the spec's
acceptance criteria in the body (ask before creating — it's outward-facing).

If the idea is trivial enough that a full spec is overkill (a one-field tweak, a copy change), say
so and offer a 3-line inline brief instead of creating a file.
