# Spec NNNN: <Title>

- **Status:** Draft | Agreed | Implemented
- **Date:** <YYYY-MM-DD>
- **Author:** <you>
- **Ticket:** #<GitHub issue number> (<M{milestone}-{nn} id>)
- **Related:** <ADR-NNNN / sibling spec / knowledge-base entry / branch>

## Business context

<2-4 sentences of BRD: who needs this, what problem it solves in the translation/patching flow,
and why now. The "why" a future reader (or the AI) needs before touching scope.>

## Goal

<1-2 sentences: what a user can do afterwards. The problem, not the solution.>

## In scope

- <what this delivers>

## Out of scope

- <the boundaries — what this explicitly does NOT do (prevents scope creep + AI guessing)>

## Business rules & edge cases

<The part only you know. One bullet per rule. Be explicit about boundary behavior:
file missing, malformed line, empty export, game already running, repeated patch, mid-update.>

- <rule>

## Contract

- **Trigger:** <CLI verb + args (e.g. `lotro patch <name> [-d path]`), or API route from M3 on>
- **Input:** <command/query record — name the real type>
- **Output:** <response record + what the CLI prints / status codes>
- **Errors:** <which `Result` failures map to which exit code (0/1/2/3/4) or ProblemDetails>
- **Files touched:** <DAT, version file, translations, backups — paths and when>

## Acceptance criteria

<Phrase each as a testable "done when…", so it maps 1:1 to a unit/E2E test. Mirror (and refine)
the ticket's criteria.>

- [ ] <criterion>
- [ ] <criterion>

## Open questions

<Empirical questions: answer from `docs/knowledge-base/` or code, cite the source; only genuinely
untested behavior stays open as `[needs live test]`. Business decisions: the AI extracts them,
YOU answer them. Never let an answer here be invented.>

- <question>

## Assumptions

- <what we take as given>
