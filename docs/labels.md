# Issue labels

One taxonomy, **shared with TheKittySaver** (`~/RiderProjects/TheKittySaver/docs/labels.md`).
Same names, same colours, same descriptions in both repos — only the `area-*` values and the
release-gate label differ, because the codebases differ. A label change in one repo is ported
to the other in the same session.

Five axes. A well-formed ticket carries **one `priority-*`**, **one `type-*`**, and
**zero or more `area-*`**. `type-*` has no exception: a QA scenario is `type-test`, a decision
ticket carries the type of the work it will produce plus `question`. A bug **a tester filed** also
carries **one `severity-*`** — priority and severity answer different questions and are documented
together below.

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

`severity-*` is **required on a bug a tester filed and on any `escaped-to-prod` bug, optional
everywhere else**. Testers set it themselves on every bug they file — it is their judgement and
nobody is better placed to make it. A bug the owner or an agent session filed may go without one;
`priority-*` already answers the queue question. That asymmetry is deliberate, not laziness: a
guessed severity is worse than a blank, because `escaped-to-prod` crossed with severity is the only
number that says whether this QA program pays for what it costs, and invented values turn it into
noise. Backfilling the 27 open bugs that predate this wording was considered and rejected for
exactly that reason (TheKittySaver #752, LotroKoniecDev #787). `priority-*` stays a queue decision
the owner can correct afterwards.
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

## Title convention

A title carries only what the labels can't say. No cargo-cult prefix. **The rules below are the
same in both repos**; only the identifier list, the examples and the retrofit history are per-repo.
They drifted apart once (TheKittySaver #752, LotroKoniecDev #787) and the drift made the same title
correct in one repo and a violation in the other.

- **No `type-*`/`area-*` echo.** Drop a leading tag that is *nothing but* a label's own word —
  `BUG:` / `Bug:` / `[BUG]` / `[Bug / UX]`, `Perf:`, `FE:`, `API:`, `Infra:`, `CI:`, `Docs:`,
  `Domain:`, `Tests:`, `Patcher:` — the label already says it, so the tag is pure decoration.
  Default shape is a plain sentence: capitalize the first word (unless it's a literal, e.g. a
  filename like `patch.bat` — don't re-case those), no trailing period.
- **A topic lead-in that is not a label synonym stays.** `Game versions:`, `Flaky test:`,
  `Search:`, `Org badge:`, `GetPersons:`, `File storage:` name a subsystem, screen, endpoint or
  file, which is information no label carries. Only the label-echoing tags go. A named system can
  also go **into** the sentence where that reads better, but leading with it is not a violation.
- **Keep a prefix when it is a real identifier used elsewhere** — this repo's milestone/epic
  codes: `M{n}-NN:`, `TP-NN:`, `UR-NN:`, `SEC-NN:`, `PERF-NN:`, `LEGAL-NN:`, `OBS-NN:`,
  `QA-FE-NN:`, `WIKI-NN:`, `[Epic] …`. Those double as the release-gate tracking this repo uses
  instead of `release-*` labels (see above) — never drop them, and never invent a new one without
  a real cross-reference behind it.
- **A bug tied to one QA test case** names the test case as a trailing parenthetical —
  `… (QA-FE-09-TC11)` — never as a leading `BUG: QA-FE-09-TC11 — …`.
- **An epic carries three signals and needs all three**: the `epic` label, an `[Epic] ` title
  prefix, and a trailing `tracking)` marker — extra words in the same parenthetical are fine,
  as in `(post-M7, tracking)`. The first two are **deliberately redundant to the tooling** —
  the picker's jq drops a ticket that carries the `epic` label *or* whose title starts with
  `[Epic]`/`[Tracking]`, that second test hard-coded and independent of `LOOP_SKIP_TITLES`
  (`scripts/claude/next-ticket.sh`). Either one alone keeps the loop off it, so neither repo's
  drift was ever a loop bug. The reason to require both anyway is the person reading the list:
  GitHub has no native epic, the label chip is easy to miss in a filtered list or a search result,
  and the title is always read — an epic without the prefix reads as ordinary work. The suffix says
  the ticket has no work of its own. Each repo had drifted to a different half: all five epics here
  carried prefix and suffix with no label at all, and in TheKittySaver #748 carried the label with
  no prefix (TheKittySaver #752, LotroKoniecDev #787).
- This was retrofitted onto the 3 open issues that had drifted (#544, #658, #738) on 2026-09-07,
  mirroring the same cleanup in TheKittySaver (~35 titles there) — see that repo's `docs/labels.md`
  for the full before/after list. Closed issues were left alone then and stay out of scope.

## Epics — how children are attached

The three signals above say a ticket **is** an epic. This says what is **under** it.

**An epic's children are GitHub sub-issues, never a list in the body.** GitHub renders them as a
panel above the description — the child rows plus a progress bar — and stamps a parent link on each
child. That panel is the record of what belongs to the epic, and it is the whole reason an epic
exists: opening one answers "which tickets are under this?" without reading prose. Wire them with
`gh issue edit <epic> --add-sub-issue 12,13,14`, in the order the series reads rather than by issue
number.

Three things to know before you wire one:

- **One parent per issue, and a second `--add-sub-issue` does not error — it silently reparents.**
  Adding a ticket that already sits under another epic *steals* it, with a success response and no
  warning. Run `gh issue view <n> --json parent` first whenever a ticket could belong to two epics.
  When two epics both want one, the epic whose identifier series the ticket carries wins — unless
  the other epic's body explicitly claims it out of that series, which is a deliberate promotion
  and beats the prefix.
- **A markdown task list creates no relationship at all.** `- [ ] #123` in a body renders a
  checkbox and binds nothing: the child's `trackedInIssues` stays `0`. It was pure decoration in
  six of the nine epics across both repos until TheKittySaver #754 / LotroKoniecDev #789.
- **An epic can nest under another epic** (GitHub allows 8 levels, 100 children per parent). That
  is the answer when a tracking parent would otherwise have to steal another epic's tickets —
  TheKittySaver's release gate holds its two QA epics that way.

**Checkboxes in an epic body mean acceptance criteria for the epic itself, and nothing else.** A
child list in the body is commentary — running order, dependencies, notes — so it uses plain
bullets. Two sources of completion state, one live and one hand-ticked, is how they drift.

## Process and state

| Label | Meaning |
|---|---|
| `epic` | Tracking parent that only groups child tickets — the loop never works it. The title carries `[Epic] ` and ` (tracking)` too (**Title convention**), and the children are sub-issues (**Epics**) |
| `audit` | Finding from an autonomous audit session — triage before `/backlog` |
| `loop-blocked` | `claude-loop`: needs human input |
| `qa` | Manual QA / test scenario |
| `qa-blocked` | Manual QA: a scenario cannot run until the owner supplies a precondition |
| `escaped-to-prod` | Found by a user in production, not by QA and not by CI — the escape-rate signal |
| `post-mvp` | Parked beyond the current release gate — do not work it before MVP ships |

`post-mvp` is this repo's release gate. TheKittySaver, already past MVP, uses `post-v1` plus
`release-mvp` / `release-v1` for "required for that release"; this repo tracks the same thing
through the `M{milestone}-{nn}` title prefix instead.

The picker skips `loop-blocked`, `epic`, `question`, `wontfix`, `invalid`, `duplicate`, `qa`,
`qa-blocked`, `audit` and `post-mvp` by default (`LOOP_SKIP_LABELS`) — **the same list as
TheKittySaver**, whose last entry is its own parking label `post-v1` instead. See
`docs/claude-loop.md`.

## Housekeeping

GitHub defaults, identical in both repos: `question`, `duplicate`, `invalid`, `wontfix`,
`good first issue`, `help wanted`.

Dependabot applies `dependencies`, `github_actions`, `.NET` and `docker` to its own PRs.
