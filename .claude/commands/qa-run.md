---
description: Manual-QA run report — build the run skeleton for a QA ticket, or check a posted run comment against the tester wiki's rules before the ticket is closed
argument-hint: #<qa-issue> [template | check]
---

Work with the **run report** of a manual-QA ticket — the one comment per execution that replaced
the per-run Google Sheet and the Drive evidence folder on 2026-09-03 (#767):

> $ARGUMENTS

The tester wiki (`Workflow-testera.md` §5) owns the format and every rule about it. This command
applies those rules; it never restates them inside a ticket and never invents new ones. If the wiki
and this file disagree, the wiki wins — fix this file.

Modes: `template` (the default) and `check`. The argument is the QA issue number.

## What a run report is

One comment under the QA ticket, posted from the skeleton when the run starts and edited until it
ends. Its grammar, which both modes rely on:

```markdown
## Run — YYYY-MM-DD — @nick

**Environment:** https://staging.lotro-translator.pl — <browser / OS>
**Result:** n PASSED / n FAILED / n BLOCKED / n NOT RUN — pass rate nn%

- TC01 PASSED
- TC03 FAILED #123                  ← the bug number is mandatory
  - Actual: <what really happened; app messages quoted 1:1, in Polish>
  - ![screenshot](…)                ← evidence is mandatory
- TC04 PASSED — one-time            ← evidence is mandatory: a state-destroying step
  - ![screenshot](…)
- TC05 NOT RUN — owner-assisted     ← the reason is mandatory
- TC06 BLOCKED — <what is missing>  ← the reason is mandatory

**Conclusion:** <one or two sentences>
```

A run comment is recognised by its first line, `## Run — `. Everything else under the ticket is a
question or noise, never a run. A second run comment is a second run (another browser, a re-run
after the ticket was corrected), never a continuation of the first.

## `template` — the skeleton for a ticket that predates `## Run report`

Every ticket written with `/qa-ticket` after #767 already ends with a `## Run report` block, and
the tester copies it with the code block's copy button. This mode exists for
every ticket written before 2026-09-03.

1. `gh issue view <n> --json title,body`. Extract every step id in document order: the `**TCkk**`
   that opens a checkbox under `## Test scenarios`. Keep gaps — ids belong to steps, never to
   positions (`/qa-ticket` §4). A ticket with **no ids at all** gets positional ones, `TC01` being
   the first checkbox, and the skeleton says so under its header
   (`_ids counted by position — the ticket carries none_`): that is the sentence the wiki makes
   the tester write by hand otherwise.
2. One line per step: `- TCkk NOT RUN`. A step tagged `_(owner-assisted …)_` becomes
   `- TCkk NOT RUN — owner-assisted` right away. A step tagged `_(one-time …)_` stays a bare
   `NOT RUN` — the `— one-time` marker is the tester's, written when the step passes.
3. Wrap the header and the lines in a ```markdown fence under `## Run report`, with the one-line
   instruction `/qa-ticket` §4 shows. `Result` starts as `0 PASSED / 0 FAILED / 0 BLOCKED / <total> NOT RUN`.
4. Print it. Then, **only if asked**, append the block to the ticket body
   (`gh issue edit <n> --body-file …`, keeping every checked box and every other byte intact) or
   post it as a comment. Appending to the body is the better default — a body block survives the
   runs that pile up below it — but both are outward-facing edits to a ticket a real person is
   about to act on: ask first.

## `check` — lint the newest run before anyone closes the ticket

1. Read the body (ids, tags, checkbox state) and every comment
   (`gh api repos/{owner}/{repo}/issues/<n>/comments --paginate`). Take the **newest** run comment
   unless a specific comment URL was given. No run comment → say so and stop; never invent one.
2. Parse it: header (date, nick), `Environment`, `Result`, every `- TCkk STATUS …` line with its
   indented sub-bullets, `Conclusion`.
3. Apply the wiki's rules. Each is one row of the report, `OK` or `PROBLEM` with the offending line:

   | # | Rule (wiki §3, §5, §6) |
   |---|---|
   | 1 | Every id from the ticket appears exactly once; no id the ticket does not carry; positional numbering is declared under the header when the ticket has no ids |
   | 2 | The status is one of `PASSED` / `FAILED` / `BLOCKED` / `NOT RUN`, upper-case, nothing else |
   | 3 | Every `FAILED` line names exactly one bug `#n`, has an `Actual:` sub-bullet and an evidence sub-bullet (an image, a video, or a quoted message) |
   | 4 | That bug exists (`gh issue view`), carries `type-bug`, and its title cites `QA-FE-nn-TCkk` for this ticket and this step |
   | 5 | Every `BLOCKED` and every `NOT RUN` line has a reason after ` — ` |
   | 6 | Every `PASSED` on a step the ticket tags `_(one-time …)_`, and every line the tester marked `— one-time`, has an evidence sub-bullet |
   | 7 | The `Result` counts equal the list; pass rate = PASSED / (PASSED + FAILED), executed steps only; `Conclusion` is present and not empty |
   | 8 | Checkboxes match: every `PASSED` step is ticked in the body and nothing else is. A mismatch is reported, never fixed by you — the tester ticks their own run |
   | 9 | No secret in the text: a password, a token, `code=` / `token=` in a URL, an activation or reset link, an unmasked e-mail that is not a `+alias` |
   | 10 | The header names the environment URL and a browser / OS |

4. Print the table, then one verdict line: `RUN OK — the tester can close the ticket` when every
   row is `OK` and nothing is `BLOCKED`; `RUN OK — stays open (BLOCKED: TCkk …)` when blocked
   steps are the only problems; otherwise `RUN INCOMPLETE — <count> problems`. Never post the
   verdict as a comment and never close the ticket unless explicitly asked: the tester closes
   their own run (wiki §6), and the owner's job at this point is the same-day verdict on each bug
   (`/qa-ticket` §5).

## Executing a run yourself

Only in an interactive session with the Claude in Chrome tools — the browser bridge is an extension
MCP that does not exist under `claude -p`, so this never belongs in a loop script. Follow the
ticket literally, step by step, the way the wiki tells a tester to: no improvised preconditions, no
faults simulated from DevTools. Write the report in exactly the grammar above and hand it back as
text. Screenshots enter a GitHub comment only through the comment editor (drag-and-drop, paste, or
the browser's file upload) — `gh` cannot attach an image — so the evidence step stays with whoever
posts the comment.

## Hand back

Print the skeleton or the check table with its verdict, then name the outward-facing action you
propose (append the block, post the comment, edit the ticket) and wait for a yes.
