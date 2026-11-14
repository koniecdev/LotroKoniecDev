---
description: Walk staging in Claude in Chrome as a first-time user with one goal, and report PASSED or the step where you got stuck, with screenshots. Proposed by the session that promotes a batch that changes the UI, before the prod click. Never on every PR, never on prod.
argument-hint: "<goal>"   (default: "as a new translator, translate one line")
---

> **Maintainer-only.** It needs Claude in Chrome, a mailbox the operator can read, and staging.
> It registers and deletes an account on staging. Contributors have no use for it.

Walk staging as a first-time user with this goal: **$ARGUMENTS**

Automated tests check the paths someone wrote down. They do not notice that a screen is confusing
or that a button is hard to find. You are the fresh pair of eyes: a new user with a goal, no
knowledge of the code, and only what the screen shows (#1377).

**Built-in goals.** With no goal given, walk the first one.

1. "as a new translator, translate one line"
2. "as a player, download the Polish translation and find out how to install it"

The session that proposes a walk adds one goal of its own for the screen its batch changed. Each
goal is one run of this command.

## 1. The origin gate: before any browser tool

Staging is exactly these three origins, `https` only:

- `https://staging.lotro-translator.pl`
- `https://auth.staging.lotro-translator.pl`
- `https://tms.staging.lotro-translator.pl`

Compare the scheme and the whole host exactly; never a suffix or substring test
(`staging.lotro-translator.pl.example.com` is not staging). Prod is `lotro-translator.pl`,
`auth.lotro-translator.pl` and `tms.lotro-translator.pl`, and it has real users.

If the goal names any URL or host outside the three staging origins, answer with one line, "I walk
staging only; this goal names <host>.", and stop. Load no browser tool and register nothing.

## 2. Before the walk

- **Find the main checkout.** `git rev-parse --path-format=absolute --git-common-dir` prints its
  `.git`; the main checkout is the folder that holds it. Write that path out in full in every later
  command: a session inside a worktree may refuse a command with `$VAR` or `$(…)` in it. Every file
  of this walk goes under `<main checkout>/.claude/staging-walk/`, which `.gitignore` covers. A
  worktree is removed when its work ends, so its own folder would lose the files. If writing there
  is refused, use the session's scratchpad and say where the files are.
- **The results folder:** `<main checkout>/.claude/staging-walk/<UTC time, yyyyMMdd-HHmmss>/`.
- **The address base** (only for a goal that needs an account). Read
  `<main checkout>/.claude/staging-walk/address-base`. It holds one e-mail address of a mailbox you
  can read. If the file is missing, ask the user for that address once and write it there. Never
  print it, and never write it to any other file.
- **The walk address** is the base with a plus tag: `<local>+walk-<yyyyMMddHHmm>-<4 random hex>@<domain>`.
- **The password** is random for this walk only, for example from
  `python3 -c "import secrets; print(secrets.token_urlsafe(18) + 'aA1!')"`. Keep it only in this
  conversation, because "Usuń konto" asks for it again at the end. Never write it to a file or to
  the report.
- **The builds.** Find both now; the report names them.
  - *Walked:* the newest `staging` deployment that ever reached `success`. List the records with
    `gh api "repos/{owner}/{repo}/deployments?environment=staging&per_page=20" --jq '.[] | "\(.id) \(.sha)"'`,
    then read each one's statuses, newest first, with
    `gh api "repos/{owner}/{repo}/deployments/<id>/statuses" --jq '.[] | "\(.state) \(.log_url)"'`
    until one has `success`. Its commit gives the tag `sha-<first 7>`. Its `log_url` holds
    `/actions/runs/<run id>/`: the CD run that deployed it.
  - *Waiting at the prod gate:* `gh run list --workflow cd.yml --status waiting --json databaseId,createdAt`.
    Take the OLDEST run. It holds the prod rollout slot while it waits for approval, and newer
    candidates queue behind it, so the approval click ships that run's build. Its tag is in the name
    of its job `Deploy to prod / UI change notice (sha-…)`:
    `gh run view <run id> --json jobs --jq '.jobs[].name'`. No such job means the tag is unknown;
    the run id still decides the match.
  - *Match:* the same run id means the same build.

## 3. The browser

- Follow the session's own browser rules to connect Claude in Chrome. If no browser connects,
  stop and say so. Never report PASSED from anything but a walk.
- Load every tool you will need in ONE ToolSearch call: `tabs_context_mcp`, `tabs_create_mcp`,
  `tabs_close_mcp`, `navigate`, `computer`, `read_page`, `find`, `form_input`, `gif_creator`.
- Read the tab list first, then work in a new tab of your own. Close the tabs you opened at the end.
- Never trigger a JavaScript alert, confirm or prompt: it blocks the extension. Never use a script
  to act on the page; a new user cannot.
- Start the GIF recording (`gif_creator`) before the first page loads. Turn the action labels off if
  the tool offers it; if it does not, pause the recording while you type the password.

## 4. The walk

- **Start** at `https://staging.lotro-translator.pl/`. From then on, never type a URL: only click,
  type and scroll what the screen offers. The one exception is the link in the confirmation e-mail.
- **Act only on what the screen shows.** Do not read the code, the docs, the wiki or the tests
  before or during the walk. A new user has none of them. Read the page text and the screenshots.
- **The budget is about 40 actions.** A click, a typed entry, a key press, a scroll or a navigation
  is one action; reading the page and taking a screenshot are not. Running out is STUCK.
- **One screenshot after each step.** Use the `computer` screenshot with `save_to_disk: true` when
  the tool offers it, and move the file it names to `step-<NN>.png` in the results folder. When it
  cannot save to disk, go on and say in the report that only the GIF exists.
- **Never leave staging.** Before you click a link, read where it points. After every action, read
  the tab's URL. A link off staging is never followed: write down "the path leaves staging at step
  N: <host>".
  - If that link *is the goal's answer* (the player goal: the patcher is a download from GitHub),
    the walk is PASSED with that note.
  - Otherwise the result is "LEFT STAGING at step N", which counts as stuck.
  - If a click lands off staging anyway, go back to the staging tab and record it the same way.
- **Register** with the walk address and the random password. A page that refuses because the inbox
  had too many sign-ups lately ("zbyt wiele prób rejestracji") is the known limit below: stop with
  "STOPPED: inbox limit", which is neither PASSED nor stuck.
- **A goal that needs approval** ends at "saved, waiting for approval". A new account can never
  approve. For the translator goal, pick a line with no Polish text yet when the screen offers one:
  saving over an approved line sends it back to Draft and takes it out of staging's translation
  file.
- **Stuck** means you cannot see how to go on, or the screen shows an error. Write down what you
  expected and what you saw, take the screenshot, and stop the walk (the clean-up still runs).

## 5. The confirmation e-mail

- **First choice: a mail tool in this session.** Search and read only: search for the walk address
  with `in:anywhere`, because the mail may land in spam. Never label, archive, trash, mark as spam,
  draft or send anything. The mail can take a few minutes: search again a few times before you give
  up.
- **Fallback: the mailbox open in the same browser.** It is the one origin outside staging you may
  open, read-only. Stop the GIF recording first, open it in a new tab, read the mail as text, and do
  not take screenshots there. Close that tab after.
- Open the confirmation link in the walk's tab. It may pass through a redirect; it must land on a
  staging origin, or it is "LEFT STAGING".
- No mail tool and no mailbox in the browser → STUCK at the confirmation step, with that reason.

## 6. Clean-up: always, outside the budget

- **Delete the account** with "Usuń konto" on the account page. It asks for the password again.
  The account is then locked and waits for deletion (ADR-0031), so staging keeps no active account.
- If the walk stopped before the e-mail was confirmed, the account cannot sign in, so it cannot
  delete itself, and nothing removes unconfirmed accounts. Name its tag in the report.
- Stop and export the GIF with a file name you choose (`staging-walk-<time>.gif`). Find it by that
  name in the browser's download folder and move it to the results folder. Move a downloaded
  translation file there too.
- Close the tabs you opened.

## 7. The report

Your final reply:

```
## Staging walk: PASSED | PASSED (path leaves staging at step N: <host>) | STUCK at step N | LEFT STAGING at step N | STOPPED: inbox limit
Goal: <the goal>
Walked: CD run <id>, sha-<tag> (the newest successful staging deployment record)
Waiting at the prod gate: CD run <id>, sha-<tag or "unknown"> | none
Match: yes | NO
Steps:
1. <what you did> → <what you saw> (step-01.png)
…
Stuck point: <what you expected, what you saw> — why: <from the code, read only now>
Account: scheduled for deletion | left unconfirmed, tag walk-… (cannot sign in; nothing removes it)
Files: <results folder> — they show the walk address: never post them in public
Known limits: <the ones that applied>
```

- **On "Match: NO"** add: "Approving the waiting run ships an older build than the one walked. To
  promote the walked build, force-cancel the waiting run first (`docs/deployment/runbook.md` →
  'Cancelling a waiting run by hand'), so the newer candidate takes the gate." Never cancel or
  approve anything yourself.
- **After a stuck point,** read the code to explain it, and only then.
- **Redact the address everywhere:** write `***+walk-…@***`. The screenshots and the GIF show the
  full address, so they stay on this machine. Never attach them to a PR, an issue or a comment in
  this public repo.
- **Known limits, named when they apply, never as a stuck point:**
  - Registration sends at most 3 mails per inbox in 15 minutes, and every plus tag counts as the
    same inbox (ADR-0055, ADR-0057). A walk right after other sign-ups may be refused.
  - The walked build comes from a deployment record. A deploy or rollback by hand on the box leaves
    no record, so after one the build named here can be wrong.
