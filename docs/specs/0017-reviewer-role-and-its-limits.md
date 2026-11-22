# Spec 0017: The Reviewer role, given by the admin, lifts the request and save limits

- **Status:** Implemented
- **Date:** 2026-11-21
- **Author:** loop worker for #1488
- **Ticket:** #1488
- **Related:** #1368 (the right to approve, later), #1483 (closed by #1488), spec 0014 (the review flow,
  on its own branch), ADR-0049, ADR-0054 and ADR-0072 (all amended 2026-11-21), ADR-0040, ADR-0041,
  wiki `Wersje-systemu` (the Recenzent line under "Wersje 1.x")

## Business context

Our regular translators work fast, and the translation API stops them with "Too many requests": it
allows 100 requests a minute per address, and one translated row costs about seven or eight. The same
people also reach the daily save limit. Raising both limits for everyone would give the same room to
every new account, a bot included. There was no role between a fresh account and the admin, and no
page to give anyone a role.

## Goal

The admin can mark a trusted translator as a Reviewer (Recenzent) on a page of the website, and a
Reviewer works without hitting the limits meant for fresh accounts.

## In scope

- A third role, `Reviewer`, seeded like `Admin` and `Translator`.
- Admin-only auth endpoints: list accounts, give the role, take it away. Each real change ends the
  person's sessions.
- A refresh or code exchange whose roles no longer match the account is refused, so a missed revoke
  cannot leave the person signed in.
- An admin page on the website, "Konta i role", reached through the auth discovery document.
- The TMS per-minute limit counts a Reviewer per account at 300 and sets no limit for the admin; the
  daily save budget skips a Reviewer as it skips the admin.

## Out of scope

- The right to approve, to reject and the author rule: #1368, after #50 and #1345. A Reviewer still
  gets 403 on both approve endpoints and sees no approve button.
- Roles per language (#48, post-MVP).
- A change to the auth server's own limits or to the translation-file download limit.
- Fewer requests per translated row (a follow-up idea, see Follow-ups in the PR).

## Owner decisions (from the ticket, 2026-11-21, #1368, and the ticket comment of 2026-11-22)

- Plain accounts keep 100 requests a minute per address and the daily save budget.
- The trusted room comes through the role the wiki and #1368 already name: Recenzent.
- A Reviewer gets 300 requests a minute, counted per account, not per address.
- A Reviewer has no daily save budget, like the admin (decision of 2026-11-16 on #1368).
- Only the admin gives or takes the role; after a change the person is signed out and signs in again
  with the new role. A new registration still gets only `Translator`.
- The admin has no per-minute limit on the TMS API, so the owner can run scripted work through the
  admin account (2026-11-22).
- The rules below started as the session's own calls; the owner confirmed them on 2026-11-22.
- The #1478 rule for query options also covers the sign-in server's API, in #1504 after this spec;
  the account list keeps `[FromQuery] int page` until then.

## Business rules & edge cases

- **Who may get the role:** an account that holds `Translator`, does not hold `Admin`, is not a
  Reviewer yet and has no deletion scheduled. Otherwise the grant answers 422
  `Auth.ReviewerRoleNotAllowed` (the repo maps a state conflict to 422). An admin already has all a
  reviewer has, a reviewer keeps `Translator` because every translation endpoint asks for it, and an
  account waiting for deletion is about to go. So an admin cannot change their own role.
- **Taking the role away is never refused** for the account's state, a deletion-scheduled account
  included.
- **Both actions are safe to repeat.** The website retries a write that timed out. A grant on an
  account that already is a Reviewer, or a removal on one that is not, answers 204 and changes,
  revokes and logs nothing.
- **Overlapping calls are safe too.** When a retry runs while the first call is still saving, the
  second write fails. A fresh read of the roles then decides: the target state is there, so the
  answer is 204, the winner already ended the sessions, and an Information line says so (event 2793).
  With the target state missing, an Identity refusal answers 500 `Auth.ReviewerRoleChangeFailed` and
  logs an Error (event 2792), and a database error goes on to the global error handler (500).
- **100 and 300 stay constants in the code,** like the 100 before. Changing one needs a release.
- **No security stamp change on a role change.** A new stamp would break links already mailed to the
  account (cancel deletion, e-mail change undo) and end the sign-in server's cookie. Instead the token
  endpoint refuses a code exchange or refresh whose roles differ from the database (ADR-0049
  amendment). That keeps "signed out after a change" true when the best-effort revoke fails or a
  request is retried, and it also catches a role changed by hand in the database. The cached
  discovery document keyed on the website's cookie roles only starts to matter for this from #1368 on,
  when a Reviewer gets the approve links.
- **"Signed out"** means the website session ends at its next token renewal, at most about five
  minutes later. Signing in again is one click while the sign-in server's cookie lives (30 minutes,
  sliding), and needs the password after it.
- **A removed role keeps its room until the access token expires,** at most five minutes (ADR-0049).
  Saves made while exempt are not in the save ledger, so the person starts with a full budget.
- **The list** shows display name (username), e-mail and roles, 50 accounts a page, ordered by
  username, with previous and next page links. Erased accounts are left out; a deletion-scheduled one
  is listed with a "Usunięcie zaplanowane" badge and only the removal link when it holds the role.
- **Where the page is:** linked from "Moje konto" only when the auth root offers the `accounts` rel,
  which it does for the admin alone. No menu link.
- **Logs:** each real change writes an Information line naming the admin and the account (events
  2790/2791); a refused refresh after a role change writes an Information line (event 2398), and so
  does a call that lost to an overlapping one (event 2793). All are the normal result of an admin's
  action (#977).

## Contract

- **Auth API (all `AdminToken` policy: a user token with `Admin`):**
  - `GET auth/admin/accounts?page=N` → 200 `AccountListResponse { items, page, pageSize, totalCount }`
    with `AccountSummaryResponse { userId, username, email, roles, deletionScheduledAt }`; links
    `self`, `previous-page`, `next-page`, and per account `grant-reviewer` (PUT) or `remove-reviewer`
    (DELETE). A page below 1 → 400 `Auth.AccountListPageInvalid`.
  - `PUT auth/admin/accounts/{accountId}/reviewer` → 204; 422 `Auth.ReviewerRoleNotAllowed`; 500
    `Auth.ReviewerRoleChangeFailed`; 404
    `Auth.UserNotFound`.
  - `DELETE auth/admin/accounts/{accountId}/reviewer` → 204; 500 `Auth.ReviewerRoleChangeFailed`; 404
    `Auth.UserNotFound`.
  - Discovery: rel `accounts` for the admin only (the link factory replays the policy).
  - Non-admin token → 403; no token → 401.
- **Token endpoint:** code exchange and refresh refuse with `invalid_grant` when the token's `role`
  claims and the account's roles differ as sets.
- **TMS API:** the limiter runs between authentication and authorization; `fixed-by-ip` gives a
  checked token with `Admin` and a GUID `sub` a partition with no limiter, keys one with `Reviewer`
  and a GUID `sub` as `account:{sub}` with 300 permits, and keys every other caller as before with
  100. `UpsertTranslation` spends the save budget only when
  `ICurrentUserAccessor.HasDailySaveBudget()` is true (neither `Admin` nor `Reviewer`).
- **Website:** `/admin/accounts`, Static SSR, Polish labels, one form per row that follows the row's
  link with PUT or DELETE. A successful change redirects to the same page with `changed=granted` or
  `changed=removed`, and only that GET shows the message. `PostedFormTarget` sits in the load-failure
  branch and, for the page's own forms the fresh list no longer draws, in the loaded branch too, so a
  second click or a stale tab shows the list instead of a blank 400 (#1122).

## Acceptance criteria

- [x] Only the admin can give or remove the Reviewer role (Translator and Reviewer 403, anonymous
  401). After a real change every token of that person is revoked, and a refresh or code exchange
  with the old roles is refused even when the revoke did not run. Signing in again issues the new
  roles.
- [x] A new registration still gets only `Translator`.
- [x] A Reviewer can make 300 TMS requests a minute, counted per account; two Reviewers behind one
  address get 300 each; the admin is never stopped by it; a plain translator is still stopped after
  100.
- [x] A Reviewer is never refused by the daily save budget; a plain translator still is.
- [x] A Reviewer still cannot approve: 403 on approve and bulk approve, and no `approve` or
  `bulk-approve` link on a translation, the list or the discovery document.

## Open questions

- None. The owner ruled the business rules in the ticket and in its comment of 2026-11-22.

## Assumptions

- A role row is data, so no migration is needed (ADR-0023): the seeder adds the `Reviewer` row at the
  next start.
