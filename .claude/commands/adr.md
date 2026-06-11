---
description: Write an Architecture Decision Record in this repo's house format
argument-hint: <the decision / question to record>
---

Write a new **ADR** capturing this decision:

> $ARGUMENTS

## Format & numbering

- List `docs/adr/` to find the highest `NNNN-` and use the next zero-padded number.
- Filename: `docs/adr/NNNN-kebab-case-title.md`.
- **Match the house format exactly** — read `docs/adr/0001-slim-srp-handlers-instead-of-mediator.md`
  as the template and format anchor. Sections, in order:
  1. `# ADR-NNNN: <Title>`
  2. Bold metadata block: **Status** (Proposed | Accepted), **Date** (today),
     **Decision-makers** (`Solo maintainer`), **Related** (layer / tickets / sibling ADRs).
  3. `## Context` — the forces and the actual code facts that constrain the choice. Be concrete;
     cite `file:line`, type names, or `docs/knowledge-base/` findings where it sharpens it.
  4. `## Decision` — numbered sub-decisions (`### 1.`, `### 2.`…), each a crisp ruling with its
     reason. This is the part future-you and the AI will obey.
  5. `## Consequences` — `### Positive` and `### Negative / Accepted Trade-offs`.
  6. `## Alternatives Considered` — `### A.` … each ending with `Rejected. <why>` (or `Chosen.`).
  7. `## Implementation Notes` — the files/types the decision touches (bullet list).
  8. `## References` — sibling ADRs, specs, tickets, knowledge-base entries, external links.

## Rules

- Ground it in **this repo**: read the code the decision concerns before writing, so Context and
  Implementation Notes are true, not generic. If the decision contradicts an earlier ADR or an
  empirical knowledge-base finding, say so explicitly and mark the superseded ADR.
- English, terse, decision-oriented prose — match the voice of 0001. No filler.
- Default **Status: Accepted** when the user has clearly decided; use **Proposed** if it's still
  open, and ask the one question that would settle it before finalizing.
- Don't implement the change here — this command only records the decision. (Follow up with
  `/feature` or `/ticket`.)

End by printing the new file path and a 2-3 line summary of the ruling.
