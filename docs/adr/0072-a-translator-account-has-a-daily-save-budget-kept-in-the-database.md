# ADR-0072: A translator account has a daily save budget, kept in the database

**Status:** Accepted
**Date:** 2026-11-13
**Decision-makers:** Solo maintainer (owner decision recorded in #687, 2026-11-13)
**Related:** `TranslationSystem.API/Features/Translations/UpsertTranslation.cs`,
`TranslationSaveBudget*.cs`, `TranslationSystem.Persistence/SaveBudget/`, the Grafana rules in
`.docker/observability/grafana/provisioning/alerting/lotrokoniecdev-alerts.yaml`; tickets #687
(SEC-09), #50 (change history), #688 (off-site backup), #1345; ADR-0004, ADR-0008 §3, ADR-0023,
ADR-0038 §7, ADR-0050, ADR-0053, ADR-0054

## Context

Every new account gets the Translator role, and a translator can save Polish text on any row. A save
replaces the old text in place, and a save on an approved row also takes it out of the game file until
an admin approves it again. Nothing keeps the old text: there is no change history yet (#50), the
published file is refreshed in place, and the database backup reaches back only a few hours (#688).

The only limit in front of the save was 100 requests a minute per visitor address. That is about
6,000 saves an hour from one address, and an attacker gets a new budget with every new address. On
2026-11-13 the whole of prod held 705 rows with Polish, so one stolen or newly registered account
could overwrite all of it in minutes, and nothing would notice.

The rate limiter cannot fix this by itself. It runs before authentication on purpose (#829), so it
never knows the account. The TMS sends no mail of its own either: ADR-0038 §7 says a future TMS mail
would go through the broker, and nothing builds that path today.

The design that removes the problem is a review flow where a save is only a proposal and never
replaces the live text (tracked by #1345 and #50). Then an overwrite cannot destroy anything. It is a
larger change with a new model, migrations and screens. The owner ruled to build this guard now and
the review flow later.

Measured on prod, read-only, on 2026-11-13 (totals only): the busiest account made at most 9 saves in
a minute, 62 in 10 minutes, 86 in an hour and 216 in 24 hours, about 300 when the saves an approval
hid are counted. Nothing recorded whether a save replaced text, so the number of overwrites could not
be measured.

## Decision

### 1. Two budgets per account, in a rolling window, for non-admins only

A non-admin account may make **1,000 saves** in the window. Of those, **100 may replace Polish that is
already on the row** (an overwrite). The window is **24 hours, rolling**. Past either budget the save
is refused. An admin is not limited: an admin can already approve, import and delete versions, so an
edit cap protects nothing there, and a stolen admin account is a sign-in problem.

Every save on a row that already has Polish is an overwrite, whoever wrote the old text, and even when
the new text is the same. A save with the same text still takes an approved row out of the game file,
and in a stolen account "own" text belongs to the victim.

The 1,000 is more than three times the measured busiest day, so a normal session never reaches it.
The 100 is the owner's starting value, not a measured one. A translator's own corrections and the
rework after a game update count as overwrites too, so the owner may raise it for the days after a
large update.

### 2. All five numbers are settings

`TranslationSaveBudget:SaveLimit`, `SaveAlertThreshold`, `OverwriteLimit`, `OverwriteAlertThreshold`
and `WindowHours` are app settings, passed through both production compose files with their defaults,
so the owner can change them in the box `.env` without a release. A startup check stops the boot when
a limit is below 1, an alert threshold is outside 1 to its limit, or the window is outside 1 to 720
hours (30 days, the privacy policy's limit for technical data). Each of those would switch the guard or its alert off without any sign (ADR-0008 §3).

### 3. The count is kept in the database, not in memory

Each budgeted save writes one row to a new table, `TranslationSaves` (the account, the time, and
whether it replaced Polish). The budget counts the account's rows in the window. Rows older than the
window are deleted on the next budgeted save, so the table is not a history (#50 is) and holds nothing
for the GDPR export.

ADR-0053 keeps its per-account budget in memory and accepts that a restart empties it. That is fine for
15 minutes. Here the window is a day, and every deploy recreates the container (no blue/green), so an
in-memory budget would start again at zero several times a day. No existing column can give the count
either: `UpdatedAt` also moves on approve, import and removal, `SubmittedById` is replaced by the next
saver, and nothing records an overwrite.

### 4. The permit is taken first: insert, count, then hand back on a refusal

The handler loads the row, provisions the translator, and then, before the text changes:

1. deletes every row older than the window,
2. inserts this save (a raw `INSERT … ON CONFLICT DO NOTHING`, committed at once). EF Core does not
   retry raw SQL by itself, so the insert runs through the execution strategy like the other two
   statements, and `ON CONFLICT` makes a retry after a lost commit answer add nothing,
3. counts the account's saves and overwrites in the window, this one included.

Over a limit, it deletes its own row and answers 429. None of this runs in a transaction. The insert is
committed before the count, so the last of any parallel requests always sees all the others, and
parallel requests can never pass more saves than the limit. Counting first and inserting after would
let every parallel request through, and a script can run as many in parallel as it likes.

ADR-0053 §2 keeps a spent permit because a fixed window cannot hand one back. This table can, and
does on a refusal. A permit taken before a save that then fails (a 409 conflict, a database error, a
cancelled request) stays spent: handing it back would need a delete on every exit path, it cannot run
when the database is what failed, and a failed save changed no text.

### 5. The refusal is a 429 with its own error code, and the save answer wins

`Translations.SaveBudgetExhausted` when the save budget is spent, `Translations.OverwriteBudgetExhausted`
when only the overwrite budget is. When both are spent, the answer names the save budget: the editor's
overwrite sentence says that rows with no Polish can still be saved, which would then be false. The
editor shows a Polish sentence for each code and keeps the typed text. The 429 carries no
`Retry-After`: in a rolling window a permit comes back when the oldest save ages out, and there is no
single moment to name.

### 6. The owner learns about it from log lines and two Grafana rules

The TMS sends no mail, so the alert is a log line that a Grafana rule turns into a mail to the existing
`owner-email` contact point (ADR-0050).

- Every saved overwrite, by any account, admins included, writes an **Information** line
  `Translation overwritten: …`, after the save is committed. A refused or conflicting save writes none.
- Every save at or above an alert threshold, and every refusal, writes a **Warning** line
  `Translation save budget: …` with the account, the budget and the outcome as properties. It is
  written on every such save, not only the one that crosses: two parallel saves can both skip the exact
  number, and the rule looks back one hour.
- Rule `lotro-translation-save-budget` makes one alert per account, budget and outcome from the Warning
  lines. Rule `lotro-translation-overwrites-total` mails when the Information lines from all accounts
  together pass 100 in 24 hours. It blocks nothing, and it catches several accounts that each stay under
  their own budget. Its 100 and 24 hours live in the rule file, because Grafana reads no app setting.

Both rules match the template key (`"@mt":"Translation …`), never the bare text. tms-api writes a
caller's `X-Correlation-ID` header and request path into its log lines, so a bare-text filter would let
any anonymous request fire the rules. A unit test ties the rule file to the C# text, and the rule file
is a code input in `scripts/ci/classify-changes.sh`, so an edit to either runs the test.

### 7. The admin endpoints need no such guard

- `BulkApproveTranslations` changes only status, at most 100 rows per call, and never text.
- `ImportExportedTexts` never touches Polish text and keeps its 20 % removal check.
- `DeleteGameVersion` deletes only a version that no translation uses.

## Consequences

### Positive

- One account, stolen or new, can replace at most 100 Polish texts a day, and save at most 1,000.
- The owner gets a mail the same day, per account, and one for many overwrites across accounts.
- The budget survives deploys and restarts, and parallel requests cannot pass it.
- The overwrite budget and its alerts can be deleted as a unit once the review flow ships.

### Negative / Accepted Trade-offs

- **A translator's own corrections spend the overwrite budget.** A busy day after a game update can
  reach 100. The owner raises `TMS_OVERWRITE_LIMIT` for such days.
- **A retried first save counts as an overwrite.** The frontend retries a save on a 5xx or a timeout.
  When the first attempt committed and its answer was lost, the retry finds Polish on the row and spends
  an overwrite permit. This needs a lost answer after a commit, so it is rare.
- **Every budgeted save costs three small statements** and deletes old rows of every account. The
  table holds one window of saves, so this stays cheap.
- **The alerts are best effort.** They depend on the log shipping and on the hand sync of `/opt/obs`.
  tms-api logs every request before its rate limiter, so a flood can delay or drop lines on the way to
  Loki. The block itself is in the database and does not depend on the log.
- **A changed log prefix needs the `/opt/obs` sync**, because CD does not carry the rule file. Until
  the sync the rule matches nothing.
- **A Warning line on every save above a threshold** can mean up to 500 Warning lines a day for one
  account. That is the price of an alert that stays up while the account keeps going.

## Alternatives Considered

### A. Partition the rate limiter by account

The ticket's first idea. The limiter runs before authentication on purpose (#829, ADR-0053
Alternative C), so it never sees the account. The handler does the same job.

### B. An e-mail through the outbox

The outbox belongs to the sign-in server. ADR-0038 §7 would send a TMS mail through the broker, which
is a new mail path for one alert. The owner chose the log line and the Grafana rule.

### C. Rely on recovery (#50 history, #688 backup)

Both repair after the damage, and both are still open. Neither limits how much one account can
destroy in a day. They are the other half of the problem, not a replacement for the cap.

### D. A save is only a proposal (#1345, #50)

The design that removes the problem: nothing to cap and nothing to recover. It is much larger. The
owner ruled to build this guard first.

### E. An in-memory per-account counter, like ADR-0053's

The least code. Every deploy resets it, so the daily cap would multiply by the number of deploys, and
a fixed window is not the rolling one the owner ruled.

### F. Counter columns on `Translators`, updated in one statement

Atomic and with no new table, but a fixed window: an account could save twice the limit across the
boundary in two minutes. It also puts budget state on the profile row.

### G. Count from the existing `Translations` columns, or add a flag there

Wrong counts: a bulk approve would look like hundreds of saves, a row saved twice counts once, and an
overwrite cannot be seen. A flag column puts guard state on a table of about 790,000 rows and still
needs a lock.

### H. One transaction with a per-account lock, the budget row committed with the save

Exact, and a failed save would spend nothing. It needs a transaction with its retry rules around a
tracked aggregate, plus a lock held during the save, which is more to get wrong than three plain
statements for a guard meant to stay small.

### I. Stop the frontend from retrying a save, as ADR-0053 §6 does for password confirmations

One click would then be exactly one request. ADR-0053 protects a budget of 10 per 15 minutes, where
three requests per click is 30 % of it. Here three is 0.3 % of the save budget and 3 % of the overwrite
budget, and without the retry every short server error would reach the translator, who would click
again and spend the same permit.

## Implementation Notes

- `UpsertTranslation.Handler` decides `isBudgeted` with `ICurrentUserAccessor.HasOnlyRegularUserPrivileges()`
  and calls `ITranslationSaveLedger` (Persistence, `SaveBudget/`) right before `ProvideTranslation`.
  `TranslationSaveBudget.Judge` is the pure decision.
- The log text lives in `TranslationSaveBudgetLogText`; event ids 1800 and 1801.
- Migration `AddTranslationSaveLedger` only creates the table (ADR-0023). Its foreign key to
  `Translators` cascades, unlike the `Restrict` keys on `Translations`: a budget row is not work.
- After the merge the owner syncs `/opt/obs` on the staging box (runbook, Observability). Setting a
  threshold of 1 in staging's `.env` for a moment proves the rule end to end.

## References

- #687 and its owner decision of 2026-11-13
- ADR-0053 (the per-account budget in the sign-in server), ADR-0050 (alerts), ADR-0038 §7 (TMS mail)
- `docs/deployment/runbook.md` → tms-api settings, Observability → Alerts
