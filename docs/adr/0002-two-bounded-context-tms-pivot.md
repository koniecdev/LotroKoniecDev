# ADR-0002: Two bounded contexts — TMS lifted from TheKittySaver, patcher frozen (later lifted)

**Status:** Accepted (amended 2026-06-11 spec 0001 · 2026-06-12 ReadModels/Primitives lifts · 2026-06-14 ADR-0004 · 2026-06-25 the patcher freeze is lifted — see the in-body amendment notes)
**Date:** 2026-06-11
**Decision-makers:** Solo maintainer
**Related:** whole-repo architecture (both contexts), ticket #90 (M2-01), PR #89 (CLAUDE.md pivot + planning-doc removal), ADR-0001 (no mediator)

## Context

M1 delivered the patcher: a Clean Architecture CLI (`export` / `patch` / `launch`) that is
empirically proven in the live game — translations survive chunk-based launcher updates including
the 47.2→48.0 major update, forum version is the reliable game-version signal, DAT vnum is useless
(all settled in `docs/knowledge-base/`). The next goal is a Translation Management System
(PostgreSQL + Web API + Blazor SSR + auth) so translators collaborate with a review workflow
instead of maintaining a flat file by hand.

The pre-pivot plan (`docs/PROJECT_PLAN.md`, `docs/TICKETS.md`) described a different world: one
shared Application layer serving CLI + Web API + WPF, MediatR dispatch, auth postponed to M5.
Facts that invalidated it:

- **Runtime incompatibility.** The patcher needs `datexport.dll` (x86 Windows native interop) and
  the local game DAT; the TMS targets Linux Docker + PostgreSQL. One shared Application layer
  would chain a Windows-native dependency into a Linux web deployment.
- **The integration artifact already exists.** The `||` translation file flows CLI `export` →
  TMS import and TMS export → CLI `patch`. A file contract needs no shared code; the format is
  round-trip-proven in the live game and its parser was just hardened against `||` inside content
  (M1-14, PR #106 — `src/Patcher/LotroKoniecDev.Application/Parsers/TranslationFileParser.cs`).
- **The patcher is done.** Every refactor in service of the TMS risks the proven core for
  consistency gains. The maintainer explicitly chose freezing working code over consistency
  refactors under time pressure.
- **The reference implementation exists.** TheKittySaver (`~/RiderProjects/TheKittySaver`,
  closed-source) already implements every TMS building block at production grade: VSA slices in
  the API project, DDD aggregates, Result monad, SharedKernel, a self-hosted OpenIddict +
  ASP.NET Identity auth server (~7.3k LOC: PKCE, rolling refresh tokens, RSA key rotation, rate
  limiting), Docker compose (postgres + migrator + auth-api + api), black-box testing discipline.
  These are the maintainer's own patterns, ready to lift.
- **Solo maintainer, very little time.** This is the only public portfolio repo —
  interview-readiness, speed, and maintenance simplicity are explicit goals.
- **ADR-0001 is already in force.** The patcher dispatches through in-house messaging interfaces
  with direct handler injection; KittySaver slices dispatch via `Mediator.SourceGenerator`, so
  lifted code needs a mechanical transform on entry.

## Decision

### 1. Two bounded contexts in one repo

Patcher (`src/LotroKoniecDev.{Primitives,Domain,Application,Infrastructure,Cli}`) and TMS
(`src/{SharedKernel,TranslationSystem,AuthSystem,Frontend,Utilities}/…`). No project references
across the boundary, ever: the TMS never references DAT/native code (it runs in Linux Docker);
the patcher never touches the DB (it runs on a Windows gaming box). Each context deploys to its
natural platform.

### 2. The patcher is frozen

Bugfix tickets only — no renames, no extractions, no restructuring in service of the TMS. Any
change must keep every existing test green without touching its assertions. The TMS deliberately
duplicates the tiny building blocks it needs (Result/Maybe/Error shapes, messaging interfaces —
they arrive inside the lifted SharedKernel); consolidating that duplication is at most a
post-MVP ticket.

**Amendment (2026-06-11, spec 0001):** the freeze admits exactly one additive slice — the CLI
translation-file auto-download in the launch flow (ticket M2-20): the patcher acts as a TMS
distribution *consumer* over HTTP (distribution, not integration — see Alternative E). The new
slice and its launch-flow wiring are sanctioned; refactors of existing handlers are not, and
every pre-existing test stays green with assertions untouched.

**Amendment (2026-06-25): the freeze is lifted — the patcher is *stable*, not frozen.** With the
TMS backend essentially in place, the original force (don't risk the proven core for consistency
gains under time pressure) no longer outweighs the cost of a codebase that visibly contradicts
itself — a flat `src/` bag of patcher projects beside cleanly foldered contexts, in the one public
portfolio repo. Refactors, renames and restructuring of the patcher are now allowed when they earn
their keep; the first was relocating the five projects under `src/Patcher/` (a pure `git mv` +
reference re-point, zero code change). The protective invariant that actually mattered is retained
and generalized: **every change keeps all existing patcher tests green with their assertions
untouched, and must not regress behavior proven in `docs/knowledge-base/`.** The `||` format gate
(§3) and the no-cross-reference boundary (§1) are unaffected, and this dissolves the "sole
exception" framing above — M2-20 is now an ordinary additive slice, not a sanctioned exception.

### 3. The `||` translation file is the only contract between the contexts

Each context owns its own parser/serializer. Golden fixture files + round-trip tests on both
sides guard against format drift (the patcher side has `TranslationFileParserTests` today; the
shared golden fixtures land with the TMS import/export slices). The format itself changes only
via ADR, updating fixtures in both contexts in the same change.

### 4. TheKittySaver is the canonical reference — lift 1:1, then de-mediatorize

Every TMS pattern is mirrored from the nearest KittySaver sibling (lift map in CLAUDE.md →
Architecture). One repo-wide deviation, carried over from ADR-0001: **no mediator**. Every lifted
slice is transformed on entry per the de-mediatorization recipe — in-house
`ICommand`/`IQuery` + closed handler interfaces, explicit DI registration, endpoints inject the
closed handler; validation via `IValidator<TCommand>` mapped to `Result` in command handlers,
inline in query handlers. `Mediator`/`MediatR` packages remain forbidden.

### 5. Deliberate non-lifts (YAGNI — revisit only on a real, present need)

- **`Calculators`** — adoption-domain computation with no TMS equivalent.
- **Domain events** — KittySaver dispatches them via Mediator notifications; the TMS core loop
  (import → edit → approve → export) needs none, and if a need appears the dispatcher is designed
  in-house via ADR first.

**Amendment (2026-06-12) — `ReadModels(+EF)` and per-system `Primitives` moved to lifts.** Both
originally sat on this list. Maintainer decision: the CQRS read/write split is part of the lifted
architectural identity this repo showcases, and retrofitting it after the first query slices ship
would mean rewriting every list/search handler — lifting it alongside the first aggregate costs
one thin projection instead. The shape mirrors KittySaver 1:1: `TranslationSystem.ReadModels`
(POCO read models per aggregate) + `TranslationSystem.ReadModels.EntityFramework` (their EF
configurations), mapped onto the same tables the write model owns; `ApplicationWriteDbContext`
(the `IUnitOfWork`, owns migrations) serves commands, `ApplicationReadDbContext` behind
`IApplicationReadDbContext` serves queries — query handlers never touch the write model.
`TranslationSystem.Primitives` follows as a mechanical consequence of the mirror: read models
must not reference the domain, yet both sides share the strongly-typed ID types and enums, and
in KittySaver those live in the per-system Primitives project (the `StronglyTypedId` base itself
stays in SharedKernel).

### 6. Auth ships from day 1 — the self-hosted OpenIddict AuthSystem is lifted wholesale

KittySaver's own ADR-0007 (2026-06-03) retires that server for Entra External ID, but its forces
do not transfer here: no social/passkey demand, no Azure learning goal, no credential-risk-transfer
mandate at this scale — while self-hosting keeps zero external SaaS dependency and the auth server
is portfolio material in this public repo. The exit door survives regardless: the resource server
validates standard JwtBearer over OIDC discovery, so re-pointing to a SaaS issuer later is
configuration, not a rewrite (ADR-0007 §3's seam cuts both ways). TMS endpoints are authorized by
default (public ones explicit); the first migration already carries user attribution
(`SubmittedById`, `ApprovedById`). No auth-less interim state to retrofit later.

### 7. No synchronous registration saga — lazy idempotent translator-profile provisioning

The KittySaver `RegisterUser`→`CreatePersonAsync` saga (cross-context call + rollback + orphan
cleanup) is **not** lifted. The translator profile is provisioned lazily and idempotently on the
first authenticated TMS request, keyed by the identity id (pattern: KittySaver ADR-0007 §4).
This eliminates the distributed transaction without needing an outbox.

**Amendment (2026-06-14, ADR-0004):** this principle is now concrete — the TMS owns translator
identity via a lean `Translator` aggregate (`IdentityId` + `DisplayName` + optional `Email` +
timestamps), `Translation.SubmittedById/ApprovedById` reference a local `TranslatorId` (not the bare
Auth `IdentityId`), and provisioning is a first-touch idempotent get-or-create in the write handlers.
See ADR-0004 for the full decision and the reference re-point.

### 8. CLAUDE.md is authoritative; the superseded planning docs are gone

`docs/PROJECT_PLAN.md`, `docs/TICKETS.md`, and `docs/LIVE_TEST_RESULTS.md` were deleted in PR #89
(the empirical findings live on in `docs/knowledge-base/`). Open GitHub issues predating the
pivot may still describe the dead world (MediatR, one shared Application, auth at M5) — where an
issue conflicts with CLAUDE.md (the operational form) or this ADR (the decision record), the docs
win; align the ticket before coding.

## Consequences

### Positive

- The empirically proven patcher carries zero refactor risk while the TMS grows beside it.
- TMS work is assembly, not design: mirror the sibling slice → de-mediatorize → wire. The fastest
  credible path for a solo maintainer, with conventions uniform across both contexts per ADR-0001.
- Each context deploys to its natural platform — no x86 native interop in the Linux web stack, no
  database on the gaming box.
- Auth and user attribution exist from the first migration; nothing to retrofit.
- The public repo demonstrates both a native-interop CLI and a full web platform (VSA, DDD,
  CQRS read/write split, OpenIddict, Docker, integration tests against real PostgreSQL) — the
  portfolio goal.

### Negative / Accepted Trade-offs

- Duplicated building blocks (Result/Maybe/Error, messaging interfaces) between patcher
  Application and TMS SharedKernel — accepted deliberately; freezing working code beats
  consistency refactors under time pressure.
- Two independent `||` parsers can drift — mitigated by golden fixtures + round-trip tests on
  both sides and the ADR gate on format changes, not by shared code.
- Owning the credential surface (the exact risk KittySaver shed in ADR-0007) — accepted at this
  scale; the JwtBearer seam keeps the SaaS exit open.
- One repo, two lifecycles: CI must keep patcher E2E Windows-skippable while TMS integration
  tests need a real PostgreSQL.
- Stale pre-pivot issues require manual alignment until the backlog re-cut completes.

## Alternatives Considered

### A. One shared Application layer for CLI + Web + WPF (the pre-pivot plan)

Rejected. Chains `datexport.dll` x86 Windows interop into a Linux Docker deployment, forces
refactors of the frozen proven patcher, and couples lifecycles that share only a file format.

### B. Separate repository for the TMS

Rejected. Two CIs, two issue trackers, and a cross-repo contract-versioning seam for a solo
maintainer, with no isolation gain over project boundaries; one repo keeps both sides of the `||`
contract honest in a single PR and tells the whole portfolio story in one place.

### C. Design the TMS fresh instead of lifting

Rejected. The slowest path under time pressure; KittySaver already encodes the maintainer's
preferred production-grade answers (auth, VSA, persistence, compose, testing) — re-deriving them
buys nothing.

### D. Lift KittySaver as-is, keeping its mediator inside the TMS

Rejected. ADR-0001 is repo-wide for cause (vulnerable Scriban transitive, runtime-only wiring,
exception-based validation); two dispatch idioms in one repo is exactly the inconsistency this
pivot exists to avoid.

### E. Integrate the contexts over HTTP or a shared database instead of a file

Rejected. The patcher must work on a Windows gaming box with zero TMS dependency; the file
contract already exists, is human-readable, and is proven to round-trip into the live game. (The
CLI launch flow (M2-20, per the §2 amendment) and the M4 WPF app merely download the translation
file over HTTP — distribution, not integration.)

### F. Follow KittySaver ADR-0007 — Entra External ID instead of the lifted OpenIddict server

Rejected for now. The driving forces there (passkeys/social demand, Azure skills alignment,
credential-risk transfer) do not exist here, and the lifted server is free to operate while
showcasing auth-server work publicly. The standards-based JwtBearer/OIDC seam preserves this as
a future config-level swap if the forces ever materialize.

## Implementation Notes

*(Decision record only — implementation lands via the re-cut M2/M3 tickets.)*

- New (M2): `src/SharedKernel/LotroKoniecDev.SharedKernel` (lift; `Mediator.Abstractions`
  dropped, in-house `Messaging/` added),
  `src/TranslationSystem/LotroKoniecDev.TranslationSystem.{Primitives,Domain,ReadModels,ReadModels.EntityFramework,Persistence,Contracts,API}`,
  `src/AuthSystem/LotroKoniecDev.AuthSystem.{API,Domain,Infrastructure,Persistence,Contracts}`,
  `src/Utilities/…` (only what's used), `compose.yaml` + `Dockerfile.migrator` +
  `Dockerfile.tests`.
- New (M3): `src/Frontend/LotroKoniecDev.Frontend` — Blazor SSR OIDC RP; `Infrastructure/`
  lifted, pages written fresh.
- Tests: `tests/LotroKoniecDev.TranslationSystem.Domain.Tests.Unit`,
  `tests/LotroKoniecDev.TranslationSystem.API.Tests.Integration` (real PostgreSQL); golden `||`
  fixture files + round-trip tests added to both the patcher unit tests and the TMS import/export
  tests.
- Untouched: all five patcher projects and `tests/LotroKoniecDev.Tests.{Unit,Infrastructure,E2E}`.
- Already done (PR #89): CLAUDE.md rewritten as the operational form of this decision;
  `docs/PROJECT_PLAN.md`, `docs/TICKETS.md`, `docs/LIVE_TEST_RESULTS.md` deleted.

## References

- ADR-0001 — Slim SRP handlers instead of the Mediator pipeline (the repo-wide deviation every
  lifted slice obeys)
- Ticket #90 (M2-01); PR #89 — CLAUDE.md pivot + planning-doc removal; PR #106 (M1-14) — `||`
  parser hardening
- `CLAUDE.md` — Architecture, lift map, de-mediatorization recipe, house rules (the operational
  form of this ADR)
- TheKittySaver (`~/RiderProjects/TheKittySaver`) — canonical reference; its
  `docs/adr/0007-adopt-entra-external-id-retire-openiddict.md` (§3 provider-agnostic seam,
  §4 lazy provisioning)
- `docs/knowledge-base/` — empirical proof the patcher core is done (update survival, version
  detection, vnum)
- CLAUDE.md "Translation file format — THE inter-context contract" — the `||` format this ADR
  puts behind an ADR gate
