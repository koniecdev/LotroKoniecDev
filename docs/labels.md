# Issue labels

One taxonomy, **shared with the maintainer's other repository**: same names, same colours, same
descriptions. Only the `area-*` values and the release-gate label differ, because the codebases
differ. A label change in one repo is ported to the other in the same session.

Five axes. A well-formed ticket carries **one `priority-*`**, **one `type-*`**, and
**zero or more `area-*`**. `type-*` has no exception: a QA scenario is `type-test`, and a decision
ticket carries the type of the work it will produce plus `question`. A bug **a tester filed** also
carries **one `severity-*`**.

## `priority-*` — how urgent (the loop reads this)

| Label | Meaning |
|---|---|
| `priority-critical` | Priority 0: blocks production, the deploy pipeline or the backlog loop |
| `priority-high` | Priority 1: worked before anything medium or low; the loop picks these first |
| `priority-medium` | Priority 2: the default for planned backlog work |
| `priority-low` | Priority 3: worth doing, nothing depends on it |

`scripts/claude/next-ticket.sh` sorts by these four, then by issue number. An unlabelled ticket
sorts last.

## `severity-*` — how bad it is for the user

Priority says how urgent a bug is **for us**. Severity says how much damage it does **to the user**.
The two drift apart: a typo on the landing page can be `priority-critical` (embarrassing in front of
users) and `severity-trivial` (nobody loses anything) at the same time. A data-loss bug in a panel
five people use is the reverse.

| Label | Meaning |
|---|---|
| `severity-critical` | Data loss, security hole, or the app is unusable for the user |
| `severity-major` | A main function is broken for the user, no workaround |
| `severity-minor` | Works but wrongly; the user has a workaround — the default choice |
| `severity-trivial` | The user loses nothing: cosmetics, typo, visual detail |

`severity-*` is **required on a bug a tester filed and on any `escaped-to-prod` bug, optional
everywhere else**. Testers set it on every bug they file: it is their judgement. A bug the owner or
an agent session filed may go without one, because `priority-*` already answers the queue question.
A guessed severity is worse than a blank: `escaped-to-prod` crossed with severity is the only number
that says whether the QA program pays for what it costs, and invented values turn it into noise. So
old bugs are never backfilled (#787). `priority-*` stays a queue decision the owner can correct
later. The tester wiki (`Workflow-testera` §8) has the same two tables in Polish.

One more label lives next to these: `escaped-to-prod`, applied by the owner to any bug a **user**
found, not QA and not CI. It is not an axis, and testers never set it. It must be applied every
time, or the count means nothing.

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
| `area-patcher` | **This repo only**: the Patcher CLI (DAT export/patch/launch) |

## Title convention

A title carries only what the labels cannot say. **The rules below are the same in both repos**;
only the identifier list and the examples are per-repo (#787).

- **No `type-*`/`area-*` echo.** Drop a leading tag that is nothing but a label's own word:
  `BUG:` / `Bug:` / `[BUG]` / `[Bug / UX]`, `Perf:`, `FE:`, `API:`, `Infra:`, `CI:`, `Docs:`,
  `Domain:`, `Tests:`, `Patcher:`. The default shape is a plain sentence: capitalize the first word
  (unless it is a literal, such as the file name `patch.bat`), no trailing period.
- **A topic lead-in that is not a label synonym stays.** `Game versions:`, `Flaky test:`,
  `Search:`, `File storage:` name a subsystem, screen, endpoint or file, which no label carries.
  A named system may also go into the sentence where that reads better.
- **Keep a prefix when it is a real identifier used elsewhere**: this repo's milestone/epic codes
  `M{n}-NN:`, `TP-NN:`, `UR-NN:`, `SEC-NN:`, `PERF-NN:`, `LEGAL-NN:`, `OBS-NN:`, `QA-FE-NN:`,
  `WIKI-NN:`, `[Epic] …`. They are also this repo's release-gate tracking (see `post-mvp` below), so
  never drop them, and never invent a new one without a real cross-reference behind it.
- **A bug tied to one QA test case** names the test case as a trailing parenthetical,
  `… (QA-FE-09-TC11)`, never as a leading `BUG: QA-FE-09-TC11 — …`.
- **An epic carries three signals and needs all three**: the `epic` label, an `[Epic] ` title
  prefix, and a trailing `tracking)` marker (extra words in the same parenthetical are fine, as in
  `(post-M7, tracking)`). For the tooling the first two are redundant: the picker skips a ticket
  with the `epic` label *or* a title that starts `[Epic]`/`[Tracking]`
  (`scripts/claude/next-ticket.sh`). Both are required for the person reading the list: GitHub has
  no native epic, the label chip is easy to miss, and an epic without the prefix reads as ordinary
  work. The suffix says the ticket has no work of its own.

## One bug class per ticket

This section covers a defect a session finds on its own: a bug, a flaky test or a missing check
that a review turns up next to the code it reads. A bug a tester files follows the tester wiki
instead (`Workflow-testera.md` §8: one bug, one report). **These rules are the same in both
repos**; only the examples differ.

A ticket for such a defect names one **class**: one kind of wrong result in one flow. It does not
name the single case someone happened to hit.

- **Name the class, and give the case you found as its first example.** The title says what goes
  wrong in general, like "The e-mail change confirm and undo pages show the wrong result", not one
  state of it. The body gives the case you found with its failure scenario, and one task: find
  every case of this class in the flow, fix each one, and give each its own test. A class can have
  more than one cause; each cause gets its own checkbox.
- **Cases with one cause always share a ticket**, even when they show up in more than one flow.
  Otherwise, a different kind of wrong result or a different flow is a separate ticket.
- **A case of the class your own ticket names is not a follow-up.** For an older ticket that names
  only one case, the class is that case's class. Finding the other cases is that ticket's task, so
  fix it in your diff with its own test, and name it under Shipped in the report.
- **Name whose job the check is before you file.** A case that asks one of our parts for a new
  check of what another of our parts sends, for an answer that part never sends by design, is not
  a defect of the part that receives it, so it is not filed. If a real path makes our part send
  that answer, file the case against that part, with the path. What always stays a finding and
  what never is: ADR-0067 §7 and §8, and ADR-0008 §3 for startup checks. This rule sorts findings;
  it does not drop any.
- **Look for a ticket of the same class before you file.**
  - **Open, and nobody has started it** (no branch, no PR, no loop worktree for it): read its body,
    add the case as a new checkbox, and write the body back with `gh issue edit --body-file`. Then
    post a comment that names the case, says where it was found and says nobody has checked it
    live. Later comments override the body and any older plan, so the comment keeps the case from
    being missed. Add any process label, such as `audit`, that a new follow-up would get.
  - **Open and already started, or closed as completed:** file a new ticket and link the old one.
    A ticket closed as not planned has already decided the case: point to it instead. Search those
    on purpose, because a keyword search over open tickets never shows them:
    `gh issue list --state closed --search "reason:\"not planned\" <key words>"`.
  - **A bug a tester filed** never gets a case added: the tester's report stays one bug. File a
    new ticket and link it.
- **Never put unrelated fixes in one ticket to save sessions.** One unclear fix would block the
  other, and the review gets less sharp. A small ticket is already cheap, because it runs in lane S.
- **Never drop a finding.** This rule changes the shape of a ticket, not the number of bugs found.
  A case that does not get a new ticket goes into an existing one. A session re-reads its ticket
  before `gh pr create` (`/ticket` step 8), so a case added after it started is never closed
  unfixed.

Why: a review reads the code around a change, and the fix for one case sits right next to the next
case. Filed one case at a time, each case costs a ticket and a full session, and each session finds
the next case: eleven tickets for two classes before this rule (#1011).

## Epics — how children are attached

The three signals above say a ticket **is** an epic. This says what is **under** it.

**An epic's children are GitHub sub-issues, never a list in the body.** GitHub shows them as a
panel above the description (the child rows plus a progress bar) and stamps a parent link on each
child. That panel answers "which tickets are under this?" without reading prose. Wire them with
`gh issue edit <epic> --add-sub-issue 12,13,14`, in the order the series reads rather than by issue
number.

Three things to know before you wire one:

- **One parent per issue, and a second `--add-sub-issue` does not fail: it silently reparents.**
  Adding a ticket that already sits under another epic takes it from that epic, with a success
  response and no warning. Run `gh issue view <n> --json parent` first whenever a ticket could
  belong to two epics. When two epics both want one, the epic whose identifier series the ticket
  carries wins, unless the other epic's body explicitly claims it out of that series (a deliberate
  promotion beats the prefix).
- **A markdown task list creates no relationship.** `- [ ] #123` in a body renders a checkbox and
  binds nothing: the child's `trackedInIssues` stays `0` (#789).
- **An epic can nest under another epic** (GitHub allows 8 levels, 100 children per parent). That
  is the answer when a tracking parent would otherwise have to take another epic's tickets.

**Checkboxes in an epic body mean acceptance criteria for the epic itself, and nothing else.** A
child list in the body is commentary (running order, dependencies, notes), so it uses plain
bullets. Two sources of completion state, one live and one ticked by hand, drift apart.

## Process and state

| Label | Meaning |
|---|---|
| `epic` | Tracking parent that only groups child tickets; the loop never works it. The title carries `[Epic] ` and ` (tracking)` too (**Title convention**), and the children are sub-issues (**Epics**) |
| `audit` | Finding from an autonomous audit session; triage before `/backlog` |
| `loop-blocked` | `claude-loop`: needs human input |
| `qa` | Manual QA / test scenario |
| `qa-blocked` | Manual QA: a scenario cannot run until the owner supplies a precondition |
| `escaped-to-prod` | Found by a user in production, not by QA and not by CI: the escape-rate signal |
| `post-mvp` | Parked beyond the current release gate; do not work it before MVP ships |
| `on-hold` | Do not merge yet: `/merge-train` skips it until a person reads the PR and removes this label |
| `needs-windows-check` | Do not merge yet: the PR changes code that runs only on Windows, and CI runs on Linux. `/merge-train` skips it until someone tries the PR on Windows, writes the result in a PR comment and removes this label |

**`needs-windows-check` is added when a PR opens (#1250).** `.github/workflows/labeler.yml` adds
it when the PR changes a path that `.github/labeler.yml` lists. The check is the full `dotnet test`
on Windows, which runs the two suites CI skips, plus the steps the PR lists under **Before merge, on
Windows**. Copy the game's `client_local_English.dat` into `tests/LotroKoniecDev.Tests.E2E/TestData/`
first (see `tests/LotroKoniecDev.Tests.E2E/README.md`); without it every E2E test skips, and the
check is not done. The PR comment gives the E2E pass count, not just "green". A session that runs
on Windows does the check itself and puts those counts in its report; the owner removes the label
when they read the PR.

The workflow runs only when a PR opens, so some PRs get no label: a later push that brings in
Windows-only code, a file moved out of the listed paths, or a workflow run that failed or never
ran. Add the label by hand then, and also when a change reaches Windows-only behaviour from another
folder, for example a new write path into the game file in the patcher Domain. The name lives in
three places: the label on GitHub, `.github/labeler.yml` and the train's skip list. Rename it in all
three at once, or the hold stops without a warning.

`post-mvp` is this repo's release gate. This repo tracks "required for the release" through the
`M{milestone}-{nn}` title prefix, not through `release-*` labels.

The picker skips `loop-blocked`, `epic`, `question`, `wontfix`, `invalid`, `duplicate`, `qa`,
`qa-blocked`, `audit` and `post-mvp` by default (`LOOP_SKIP_LABELS`); see `docs/claude-loop.md`.

## Housekeeping

GitHub defaults: `question`, `duplicate`, `invalid`, `wontfix`, `good first issue`, `help wanted`.

Dependabot applies `dependencies`, `github_actions`, `.NET` and `docker` to its own PRs. Its Caddy
PR for the compose files also gets `on-hold`, because no CI check tests that PR (ADR-0060 §4).
Renaming or deleting `on-hold` removes that hold without a warning: Dependabot drops a label the
repo does not have, and the train then merges the PR unread.
