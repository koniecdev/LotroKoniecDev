# ADR-0032: The GDPR Data Export Gains a TMS Leg, Composed by the Frontend with the User's Own Token

**Status:** Accepted
**Date:** 2026-07-12
**Decision-makers:** Solo maintainer (ticket #456, legal & GDPR compliance pack #459)
**Related:** `ExportAccountData` (AuthSystem), `ExportMyContributionData` (TranslationSystem),
`AccountEndpointsExtensions` (Frontend, LEGAL-02), ADR-0031 (deletion needs no cross-context
call), TKS ADR-0003 (service-to-service token pipeline — deliberately NOT lifted), tickets
#456, #459

## Context

`GET auth/account/data-export` returns **auth data only**. While the account exists, the
TranslationSystem holds personal data linked to the same identity (GDPR Art. 15 covers all
of it):

- the **Translator profile** (ADR-0004): `DisplayName`, `Email`, `IdentityId`,
  `ProvisionedAt` — direct PII stored in the TMS context;
- the **contribution attribution**: `Translation.SubmittedById` / `ApprovedById` rows.
  The reference is an opaque `TranslatorId`, but it is linkable to the person for as long
  as the account exists, so "auth-only is enough" cannot be justified (after erasure the
  attribution becomes non-attributable — that is ADR-0031's argument, and it holds only
  post-deletion).

So the export must cover the TMS leg. Two sanctioned shapes existed (ticket #456):

1. **auth-api aggregates (the TheKittySaver original).** TKS's `ExportAccountData` calls
   the sibling context over a back-channel HTTP client authenticated with a
   client-credentials **service token** (TKS ADR-0003: `AuthTokenClient`,
   `AuthTokenDelegatingHandler`, `OnBehalfOf` header, `RequireServiceScope` policy).
   LotroKoniecDev never lifted that pipeline — auth-api has **no** HTTP client to tms-api
   today. Lifting it for this one GET means: the token client + delegating handler +
   resilience pipeline, a `TranslationSystem BaseUrl` setting for auth-api in **every**
   environment (dev hosts, prod-parity compose, staging, prod → terraform + runbook +
   `.env` matrix churn), and a brand-new auth→tms coupling direction.
2. **The frontend composes.** The user-facing export is already the frontend download
   route (`/account/export`, LEGAL-02) — a server-side route that holds the caller's
   access token and already talks to **both** APIs through typed clients. tms-api only
   needs a plain **self-only** endpoint (identity from the caller's own bearer token via
   `ICurrentUserAccessor`); the download route fetches both legs and merges them into the
   exported JSON file.

## Decision

**Shape 2 — frontend composition.** tms-api ships
`GET /api/v1/translators/me/data-export` (authorized, self-only, read models only); the
frontend download route calls it with the user's own token and composes the file:

- Auth leg fails → the export fails (unchanged — without auth data there is no export).
- **TMS leg fails → the export still succeeds** with `translationData: null` and
  `isComplete: false` (the acceptance criterion; NB the TKS original returns 503 here —
  we deviate deliberately, a degraded export beats no export).
- The TMS payload is a **contribution summary + row identifiers**, not full row bodies:
  the Translator profile, per-status counts, and the `(TranslationId, FileId, GossipId,
  Status)` identifier list per role (submitted / approved). Source/translated texts are
  the catalog's content, not the user's personal data; identifiers prove exactly what is
  attributed. First/last activity timestamps are **not** claimed — without
  `TranslationHistory` (post-MVP) row timestamps do not reliably reflect *this user's*
  activity, and a GDPR document must not guess.

Rationale for the locus: YAGNI (house rule) — the service-token pipeline is real
infrastructure with per-environment configuration for a hypothetical direct-API consumer
that does not exist (no real users; the frontend is the only client). ADR-0031 set the
precedent that cross-context back-channels are avoided until a real need appears. The
seeded `lotrokoniecdev-api` client-credentials client and tms-api's `RequireServiceScope`
policy stay dormant (they arrived with the auth-module lift) — if a future feature needs
a genuine server-to-server call, lift TKS ADR-0003 then, via a new ADR.

## Consequences

- **`GET auth/account/data-export` alone stays auth-only.** The complete Art. 15 export
  is the frontend download — which is the only export surface the product exposes to
  users (LEGAL-02's "Moje konto" page). Acceptable while the frontend is the sole API
  consumer; revisit if a public API programme ever ships.
- The downloaded file's envelope changes from the raw auth response to a composed
  `{ authData, translationData, isComplete }` document (HATEOAS links dropped — they were
  transport metadata, not personal data). No back-compat concern: pre-launch, exports are
  point-in-time documents.
- tms-api gains one authorized read-only endpoint; no schema change, no new env vars, no
  deployment changes.
- A TMS outage degrades the export (`isComplete: false`) instead of failing it; the user
  can retry later for a complete document.
