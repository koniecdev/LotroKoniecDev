# Spec 0010: Terms of service — fan-project disclaimer + translation contribution license

- **Status:** Implemented
- **Date:** 2026-07-12
- **Author:** koniecdev
- **Ticket:** #453 (LEGAL-03) — part of the legal & GDPR pack (epic #459)
- **Related:** #452/PR#460 (LEGAL-01 two-phase deletion — the anonymized-retention design this
  license must cover), spec 0009 (LEGAL-02 account UX), #458 (LEGAL-05 footer legal links —
  depends on this), auth privacy policy `Pages/Account/PrivacyPolicy.cshtml`, TKS structural
  mirror: `src/Frontend/TheKittySaver.Frontend/Components/Pages/Terms.razor` (8 sections + TOC)

## Business context

The service has no terms of service anywhere — no frontend page, nothing on the auth server —
while production is live at lotro-translator.pl and staging allows self-registration. Two legal
gaps make this critical: (1) LOTRO is the IP of Standing Stone Games / Middle-earth Enterprises
and the platform must state its non-commercial fan-project status and non-affiliation; (2) the
LEGAL-01 erasure design deliberately **keeps translation contributions in the service
(anonymized) after account deletion** and distributes them in `polish.txt` — that retention and
distribution is only defensible if every contributor granted an explicit license, which today
exists nowhere. The privacy policy already promises this retention (§ retencja: "wkład
tłumaczeniowy pozostaje w serwisie wyłącznie z nieprzypisywalnym identyfikatorem") without the
contractual basis behind it.

## Goal

Any visitor (anonymous included) can read the terms of service in Polish; every new translator
grants the contribution license as part of registration, so anonymized post-deletion retention
and `polish.txt` distribution rest on an explicit grant instead of an assumption.

## In scope

- **Terms page on the frontend** — `Components/Pages/Terms/Terms.razor`, routes `/regulamin`
  (canonical) + `/terms` (alias, mirrors TKS), static SSR, `[AllowAnonymous]` (the frontend's
  `AuthorizeRouteView` defaults to authorized — same opt-out as `Home.razor`). Content is plain
  Polish markup (no `IStringLocalizer` — our frontend has no localization infra and all pages are
  hardcoded Polish). Structure mirrors TKS Terms: intro lede, numbered `legal-sec` sections with
  anchor ids, sidebar table of contents, "last updated" date line. Sections (adapted from TKS's
  8):
  1. **Postanowienia ogólne** — service identity, operator contact, **fan-project status and
     non-affiliation disclaimer** (SSG / Middle-earth Enterprises own LOTRO; the platform is a
     non-commercial community translation project).
  2. **Definicje** — Serwis, Użytkownik/Tłumacz, Wkład tłumaczeniowy, `polish.txt` (the
     distributed translation file), Gra.
  3. **Zasady korzystania** — acceptable use (no scraping/abuse, no unlawful or infringing
     content in translations, quality/review workflow is at the operator's discretion).
  4. **Konta** — registration, e-mail confirmation, deletion incl. the 14-day window (consistent
     with the privacy policy; link, don't duplicate).
  5. **Licencja na wkład tłumaczeniowy** — the load-bearing section: the contributor's grant
     covering storage, modification (review/edits), distribution in `polish.txt` to players (via
     the API/patcher), and **post-deletion retention in anonymized form**. Exact scope = owner
     decision (open questions).
  6. **Dane osobowe** — pointer section linking to the auth-server privacy policy
     (`{auth origin}/Account/PrivacyPolicy`), like TKS §6.
  7. **Reklamacje i kontakt** — contact channel, response time, no-warranty/liability limits
     appropriate to a free non-commercial service.
  8. **Postanowienia końcowe** — changes to the terms (procedure = owner decision), governing
     law: **Poland** (per the ticket), severability.
- **Footer link** — `MainLayout.razor` footer gains a "Regulamin" link to `/regulamin`
  (the footer today is a single copyright line; LEGAL-05/#458 will later add the full legal-links
  row — this ticket adds just the terms link so AC "reachable from the footer" holds).
- **Registration checkbox** — `AuthSystem.API/Pages/Account/Register.cshtml` gains a third
  required consent checkbox ("Akceptuję regulamin") next to the two existing ones, persisted as
  `AcceptedTermsOfService` end-to-end (page model → `RegisterRequest` → handler → domain user →
  EF column, additive N-1-safe migration). Registration without it fails validation. Existing
  accounts are grandfathered (no backfill; column defaults to false). The terms link is
  cross-origin (auth server → frontend origin), `target="_blank" rel="noopener"` like the
  existing privacy-policy link; the frontend origin comes from configuration, not hardcoding.

## Out of scope

- The full footer legal-links row and the privacy-policy accuracy pass — that is #458 (LEGAL-05),
  which depends on this ticket.
- Terms versioning/history UI, diffing, or re-acceptance flows beyond what the owner decides for
  registration.
- Any change to the deletion/anonymization backend (LEGAL-01 shipped it; this ticket only
  licenses what it already does).
- Cookie banner (#454), data-export scope (#456).
- English translation of the terms — the service is Polish-facing; `/terms` is only a route
  alias.

## Business rules & edge cases

- The terms page must be publicly readable — an anonymous visitor landing from the registration
  page must not hit the login redirect.
- The contribution-license section must explicitly name both facts the erasure design assumes:
  distribution in `polish.txt` and post-deletion retention of anonymized contributions. Vague
  "you grant us a license" wording does not satisfy the AC.
- The privacy policy and the terms must not contradict each other on retention wording; the terms
  reference the policy for personal-data matters instead of restating it.
- If the owner chooses a required checkbox at registration: registration must fail validation
  without it, the acceptance must be persisted (like `AcceptedPrivacyPolicy`), and existing
  accounts (staging self-registrations, production) need a decided treatment.
- The "last updated" date is maintained manually in the page content (no CMS).

## Contract

- **Trigger:** browser `GET /regulamin` (alias `/terms`) on the frontend; links from the frontend
  footer and the auth-server registration page.
- **Input/Output:** static SSR page — no new TMS endpoints. Registration:
  `RegisterRequest`/`Register.cshtml.cs` gain an `AcceptedTermsOfService` bool + persistence on
  the auth user (EF migration on the auth context, forward-only per ADR-0023 — additive column,
  N-1 safe).
- **Errors:** none new; unauthenticated access must NOT produce the `RedirectToLogin` flow.
- **Files touched:** `src/Frontend/LotroKoniecDev.Frontend/Components/Pages/Terms/Terms.razor`
  (+ any page CSS), `Components/Layout/MainLayout.razor`,
  `src/AuthSystem/LotroKoniecDev.AuthSystem.API/Pages/Account/Register.cshtml(.cs)`; checkbox
  option additionally touches AuthSystem Domain/Persistence + a migration.

## Acceptance criteria

- [ ] Done when an anonymous visitor can open `/regulamin` on the frontend and read the full
      terms (static SSR, no login redirect, SSR-purity gate stays green).
- [ ] Done when the frontend footer contains a working "Regulamin" link on every page.
- [ ] Done when the registration page references the terms per the owner's decision (link or
      required checkbox), and — if checkbox — registration without it fails validation and the
      acceptance is persisted.
- [ ] Done when the contribution-license section explicitly covers (a) distribution of
      contributions in `polish.txt`, (b) retention of anonymized contributions after account
      deletion, in wording consistent with the privacy policy.
- [ ] Done when the non-affiliation / fan-project disclaimer names SSG / Middle-earth Enterprises
      and states non-commercial status.
- [ ] Done when governing law is stated as Polish law and a changes-to-terms procedure (per owner
      decision) is present.

## Open questions

Empirical — answered from the repo:

- *Is the frontend page public by default?* No — `Routes.razor` wraps everything in
  `AuthorizeRouteView`; public pages opt out with `[AllowAnonymous]` (`Home.razor` does).
- *Does the frontend have localization to mirror TKS's `IStringLocalizer` Terms?* No — zero
  `IStringLocalizer` usages; all pages hardcode Polish. The page is written as plain Polish
  markup.
- *What does registration already collect?* Two persisted consents (`AcceptedPrivacyPolicy`,
  `AcceptedDataProcessingConsent`) with required checkboxes — the checkbox option has an exact
  sibling to mirror.

Business decisions — answered by the owner (2026-07-12, in-session):

1. **License scope** — non-exclusive, royalty-free, perpetual, **irrevocable**, covering storage,
   modification (review/edits), distribution in `polish.txt` to players via the API/patcher
   (sublicense), and post-deletion retention in anonymized form.
2. **Moral rights** — a non-exercise clause: the contributor undertakes not to exercise the
   attribution right against anonymized distribution and authorizes the operator to publish the
   contribution without attribution. No credits page.
3. **Registration UX** — a **required persisted `AcceptedTermsOfService` checkbox** for new
   registrations (mirror the two existing consents; additive N-1-safe migration). Existing
   accounts are **grandfathered** — their contributions rest on the current privacy policy until
   LEGAL-05 (#458).
4. **Changes-to-terms procedure** — e-mail notification of material changes with **14 days'**
   notice; a user who does not accept may delete the account. Notification is a manual operator
   action, not new code.

## Assumptions

- Governing law is Poland — stated in the ticket by the owner; not re-asked.
- The terms live on the frontend (ticket decision), not the auth server; the auth privacy policy
  stays where it is and is linked, not moved.
- Canonical route `/regulamin` with `/terms` alias, mirroring TKS.
- Operator identity/contact in the terms reuses what the privacy policy already publishes.
