# Issue labels

One taxonomy, **shared with TheKittySaver** (`~/RiderProjects/TheKittySaver/docs/labels.md`).
Same names, same colours, same descriptions in both repos — only the `area-*` values and the
release-gate label differ, because the codebases differ. A label change in one repo is ported
to the other in the same session.

Five axes. A well-formed ticket carries **one `priority-*`**, **one `type-*`**, and
**zero or more `area-*`**. A bug carries **one `severity-*`** on top of that — priority and
severity answer different questions and are documented together below.

## `priority-*` — how urgent (the loop reads this)

| Label | Meaning |
|---|---|
| `priority-critical` | Priority 0: blocks production, the deploy pipeline or the backlog loop |
| `priority-high` | Priority 1: worked before anything medium or low; the loop picks these first |
| `priority-medium` | Priority 2: the default for planned backlog work |
| `priority-low` | Priority 3: worth doing, nothing depends on it |

`scripts/claude/next-ticket.sh` sorts by exactly these four, then by issue number.
An unlabelled ticket sorts last.

## `severity-*` — how bad it is for the user

Priority says how urgent a bug is **for us**. Severity says how much damage it does **to the user**.
The two drift apart, and that is exactly why both exist: a typo on the landing page can be
`priority-critical` (embarrassing in front of users) and `severity-trivial` (nobody loses anything)
at the same time. A data-loss bug in a panel five people use is the reverse.

| Label | Meaning |
|---|---|
| `severity-critical` | Data loss, security hole, or the app is unusable for the user |
| `severity-major` | A main function is broken for the user, no workaround |
| `severity-minor` | Works but wrongly; the user has a workaround — the default choice |
| `severity-trivial` | The user loses nothing: cosmetics, typo, visual detail |

Testers set `severity-*` themselves on every bug they file — it is their judgement and nobody is
better placed to make it. `priority-*` stays a queue decision the owner can correct afterwards.
The tester wiki (`Workflow-testera` §8) carries the same two tables in Polish.

One more label lives next to these: `escaped-to-prod`, applied by the owner to any bug a **user**
found — not QA, not CI. It is not an axis and testers never set it. It is the only signal that says
whether this QA program is worth what it costs, so it gets applied every time or the count means
nothing.

## `type-*` — what kind of work

| Label | Meaning |
|---|---|
| `type-feature` | New user-visible capability or a new vertical slice |
| `type-bug` | Something isn't working |
| `type-refactor` | Internal restructuring, no behaviour change |
| `type-test` | Test coverage or test infrastructure |
| `type-infra` | Build, CI/CD, deployment, tooling or repo scripts |
| `type-docs` | Documentation only |

## `area-*` — which part of the system

| Label | Meaning |
|---|---|
| `area-frontend` | Blazor SSR frontend |
| `area-api` | HTTP API surface: endpoints, contracts, HATEOAS |
| `area-domain` | Domain layer: aggregates, value objects, domain services |
| `area-auth` | Authentication and identity |
| `area-patcher` | **This repo only** — the Patcher CLI: DAT export/patch/launch |

## Process and state

| Label | Meaning |
|---|---|
| `epic` | Tracking parent that only groups child tickets — the loop never works it |
| `audit` | Finding from an autonomous audit session — triage before `/backlog` |
| `loop-blocked` | `claude-loop`: needs human input |
| `qa` | Manual QA / test scenario |
| `qa-blocked` | Manual QA: a scenario cannot run until the owner supplies a precondition |
| `escaped-to-prod` | Found by a user in production, not by QA and not by CI — the escape-rate signal |
| `post-mvp` | Parked beyond the current release gate — do not work it before MVP ships |

`post-mvp` is this repo's release gate. TheKittySaver, already past MVP, uses `post-v1` plus
`release-mvp` / `release-v1` for "required for that release"; this repo tracks the same thing
through the `M{milestone}-{nn}` title prefix instead.

The picker skips `loop-blocked`, `epic`, `qa`, `post-mvp` and `audit` by default
(`LOOP_SKIP_LABELS`) — see `docs/claude-loop.md`.

## Housekeeping

GitHub defaults, identical in both repos: `question`, `duplicate`, `invalid`, `wontfix`,
`good first issue`, `help wanted`.

Dependabot applies `dependencies`, `github_actions`, `.NET` and `docker` to its own PRs.
