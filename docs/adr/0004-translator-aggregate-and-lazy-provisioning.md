# ADR-0004: TMS owns translator identity — a lean `Translator` aggregate, provisioned lazily

**Status:** Accepted
**Date:** 2026-06-14
**Decision-makers:** Solo maintainer
**Related:** ticket #133 (M3-09), ADR-0002 §6 (auth from day 1) + §7 (no registration saga — lazy
provisioning), TheKittySaver ADR-0007 §4 (lazy idempotent `Person` provisioning) and its
`Cat → Person → Auth` reference shape, spec 0001 (the translation domain), M3 consumers #34
(list) / #35 (editor) / #36 (mini-dashboard)

## Amendment (2026-06-24): provision on the first authenticated *request*, not the first *write*

**Supersedes §3's "first-touch in the write handlers, not global middleware".** Provisioning now
also runs eagerly on the caller's **first authenticated request** via a best-effort
`TranslatorProvisioningMiddleware` (wired after `UseAuthorization`), in addition to the existing
authoritative call inside the write handlers. This realigns the implementation with the wording
ADR-0002 §7 fixed from the start ("provision lazily and idempotently on the first authenticated
request").

**Why (the real, present need that flips the §3 YAGNI call).** Under write-only provisioning a
user who registers, confirms their email and logs in has **no** TMS `Translator` row until they
happen to perform a write. So a freshly logged-in user opening their profile / the M3 mini-dashboard
(#36) — or any "who am I in the TMS" view — would see *nothing*, because browsing never provisioned
them. That is wrong UX, not a hypothetical: the profile must exist the moment an authenticated user
first touches the TMS, regardless of whether their first action is a read or a write. (A synchronous
`Register → CreateTranslator` cross-context saga remains rejected — ADR-0002 §7; the only saga-free
place to create the row is still the TMS, on first authenticated contact.)

**Why the original §3 objection no longer applies.** §3 rejected global provisioning because it
"puts a write on the read path". It no longer does: the provisioner now **writes only when the
claims actually changed** (display name / email), so the steady state is a single indexed `SELECT`
on `Translators.IdentityId` and **no** write — the same lookup a "current translator" read would do
anyway. The middleware is **best-effort**: a `Result` failure (e.g. a token without a display-name
claim) is logged and skipped so read endpoints never depend on provisioning succeeding, and a 401/403
short-circuits before it ever runs. The write handlers keep their own provisioning call as the
**authoritative** attribution step (a write is never unattributed even if the middleware was skipped
or best-effort-failed), now reduced to that same cheap lookup once the row exists.

**Trade-offs accepted.** One extra indexed `SELECT` per authenticated request (negligible, and the
read it would already need). The aggregate keeps **only** an immutable `ProvisionedAt`: a mutable
`LastSeenAt` was dropped (migration `RemoveTranslatorLastSeenAt`) because bumping it on every request
would reintroduce the write-on-read §3 guarded against, while stamping it only on a claim change made
its "last seen" name lie and left it with no consumer — dead weight. A true activity timestamp, if
ever needed, is a separate throttled concern (YAGNI).
Everything else in this ADR — the lean aggregate (§1), `Translation.*ById → TranslatorId` (§2),
read-model-first (§4), and the idempotency guarantees — stands unchanged.

## Context

Today `Translation.SubmittedById` / `ApprovedById` are `IdentityId` value objects pointing
**directly** at the AuthSystem (the cross-context user id) — a deliberate M2 shortcut. At the time
there was no UI consumer of a human-readable identity, so the lazy-provisioning acceptance criterion
of the AuthSystem lift (#92) was descoped to "`CurrentUserAccessor` reads `IdentityId` from the
JWT", and ADR-0002 §7 recorded only the *principle* (no synchronous `RegisterUser → CreatePerson`
saga; provision lazily and idempotently on the first authenticated request, keyed by identity id).

M3 forces the concrete model. The translation **list** (#34) and **side-by-side editor** (#35)
must render *"submitted by / approved by <name>"* for **other** translators' rows. The current
viewer's JWT carries only their own claims — it cannot resolve another user's display name. Two
ways out:

- **(a)** a **local** `Translator` projection the TMS owns, provisioned lazily from claims — clean
  DDD, zero runtime cross-context coupling, a list/detail join resolves every name. This is the
  canonical KittySaver `Cat → Person → Auth` shape.
- **(b)** the TMS calls the AuthSystem API for names on every list render — N+1, and the AuthSystem
  becomes a hot runtime dependency on the read path. **Rejected.**

This is the decision ADR-0002 §7 deferred; the ticket body settles it (option **a**), and this ADR
transcribes it. It is **not** a contested business decision: §7 already fixed the provisioning
principle, and the reference shape is lifted 1:1 from KittySaver — what remains is recording the
concrete aggregate and the one reference migration.

## Decision

### 1. The TMS owns translator identity via a lean `Translator` aggregate

A new `Translator` aggregate root lives in `TranslationSystem.Domain`
(`Aggregates/TranslatorAggregate/`), mirroring the **pattern** of KittySaver's `Person`, not its
surface. `IdentityId` stays the cross-context microservice FK to the AuthSystem; everything else is
a local TMS concern.

- **In scope:** `IdentityId` (the Auth user id, unique), a `DisplayName` value object, an
  **optional** `Email` value object, and an immutable `ProvisionedAt` timestamp (a mutable
  `LastSeenAt` was considered and dropped — see the 2026-06-24 amendment).
- **Out of scope (lives in Auth, or is post-MVP):** addresses, phone numbers,
  archival/anonymization, preferred language, per-language roles, statistics. CLAUDE.md is explicit
  — "our aggregates are far simpler than `Cat` — don't inflate them"; the same discipline applies to
  `Person`. The fat `Person` surface is deliberately **not** lifted.

`DisplayName` mirrors KittySaver's `Username` constrained-string VO (NullOrEmpty + MaxLength);
`Email` mirrors KittySaver's `Email` VO but is nullable on the `Translator`, because the `email`
claim may be absent and a lean profile does not require it.

### 2. `Translation.*ById` reference a local `TranslatorId`, not the Auth `IdentityId`

The literal KittySaver shape is `Translation.TranslatorId → Translator.IdentityId → Auth`.
`Translation.SubmittedById` and `ApprovedById` become **`TranslatorId`** (a local FK to
`Translators`), replacing the bare `IdentityId`. `Translator.IdentityId` remains the single
cross-context reference to Auth. The read side joins `Translation → Translator` so list/detail
responses carry the submitter/approver as `{ id, displayName }`.

**Trade-off:** one reference migration (re-point two columns + add the `Translators` table). Cheap —
pre-release, zero users, "breaking changes are free" (ADR-0002, Project status). No back-compat
shim.

### 3. The profile is provisioned lazily, idempotently, on first authenticated write

No synchronous registration saga (ADR-0002 §7; pattern KittySaver ADR-0007 §4). The `Translator` is
provisioned by an **idempotent get-or-create keyed by `IdentityId`**, performed as the first step of
the write handlers that stamp a `TranslatorId` (upsert, approve) — "first-touch in the write
handlers". An application service (`ITranslatorProvisioner`) resolves the authenticated `IdentityId`
+ `name`/`email` claims, returns the existing `TranslatorId` if the row exists, else creates it. The
display name / email refresh from the current claims on each touch, so a renamed account converges
without a separate sync.

Idempotency is enforced at two levels: a unique index on `Translators.IdentityId` (the DB
invariant, mirroring `Person`), and a get-then-create guard that, on the rare concurrent-first-write
race, catches the unique-constraint violation and re-reads the now-committed row. Repeat requests
add **no** duplicate rows.

**Why first-touch in the write handlers, not global middleware.** Only writes that stamp a
`TranslatorId` need the row to exist; reads (list/detail) need *other* translators' display names,
which the read-model join already provides without provisioning the viewer. Scoping provisioning to
the writes that actually need it keeps it simple, keeps every read off the write path, and makes it
trivially unit- and integration-testable. A global "provision on every authenticated request" is
broader than the present need (YAGNI) and would put a write on the read path.

### 4. Read-model-first, like every other aggregate

`Translator` ships with its POCO `TranslatorReadModel` (+ EF configuration) in the same change
(ADR-0002 amendment, CQRS day 1). Query handlers join the read models; the write model is never used
to serve list/detail name resolution.

## Consequences

### Positive

- M3 list/editor resolve submitter/approver names with a single local join — no runtime AuthSystem
  dependency on the read path (option **b** avoided).
- The canonical KittySaver `Cat → Person → Auth` reference shape is preserved, now as
  `Translation → Translator → Auth`; the codebase keeps telling the same DDD story.
- ADR-0002 §7's deferred principle is now concrete and proven by tests (provisioning idempotency
  against real PostgreSQL).
- Auth attribution remains from day 1; the only change is *which* id the `Translation` stores.

### Negative / Accepted trade-offs

- One reference migration (re-point `SubmittedById`/`ApprovedById`, add `Translators`). Accepted —
  pre-release, zero users.
- A first authenticated write pays one extra round-trip (get-or-create) once per translator, then
  never again. Negligible and off the read path.
- The lean `Translator` intentionally omits everything the fat `Person` carries; if a real future
  need appears (e.g. per-language roles — already a post-MVP backlog item), it is added then, via a
  ticket, not pre-built now.

## Alternatives considered

- **(b) Resolve names by calling the AuthSystem API per render.** Rejected — N+1 on the read path,
  AuthSystem becomes a hot runtime dependency, defeats the bounded-context isolation ADR-0002
  establishes.
- **Keep the bare `IdentityId` and denormalize a display-name string onto each `Translation`.**
  Rejected — duplicates the name across every row a translator touches, drifts on rename, and still
  needs a provisioning point to capture the name; a single `Translator` row is the normalized,
  rename-convergent form.
- **Global provisioning middleware on every authenticated request.** Rejected for now (YAGNI) —
  broader than the present need and puts a write on the read path; first-touch in the write handlers
  covers every case that actually stamps a `TranslatorId`.
- **Lift KittySaver's `Person` wholesale.** Rejected — its addresses/phone/archival/anonymization
  surface is adoption-domain weight the TMS does not carry; CLAUDE.md mandates mirroring the pattern,
  not the surface.

## References

- ADR-0002 §6 (auth from day 1), §7 (no registration saga — lazy provisioning) — the principle this
  ADR makes concrete
- TheKittySaver `docs/adr/0007-adopt-entra-external-id-retire-openiddict.md` §4 — lazy idempotent
  `Person` provisioning keyed by the identity id (the lifted pattern)
- TheKittySaver `PersonAggregate` (entity / `Username` + `Email` VOs / `IPersonRepository`) and
  `PersonReadModel` (+ EF config) — the lean subset mirrored here
- CLAUDE.md — "our aggregates are far simpler than `Cat` — don't inflate them"; CQRS read/write split
  (ADR-0002 amendment); the de-mediatorization recipe
- Ticket #133 (M3-09); M3 consumers #34 / #35 / #36
