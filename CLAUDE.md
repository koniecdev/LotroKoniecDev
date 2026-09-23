# CLAUDE.md — LotroKoniecDev

> Project memory — **self-contained**: a fresh clone has everything the AI needs, with no
> machine-local config required. When a doc in this repo and the code disagree, **the code wins**:
> read the file, use what's there, and fix or flag the stale doc. The **wiki** is the one exception
> and outranks both — see "Source of truth" below.

## What this is

A **LOTRO Polish translation platform** on **.NET 10 / C# 14** (`Directory.Build.props` is
authoritative) — **two bounded contexts in one repo**, integrating through a file contract:

1. **Patcher** (shipped, **stable**) — CLI that exports English texts from the game's binary DAT
   file (`export`), injects `||`-format Polish translations back (`patch`), and launches the game
   (`launch`). An Avalonia player app (M4, ADR-0033) will reuse its Application handlers.
2. **TMS — Translation Management System** (built — M2 backend + M3 frontend delivered, deployed
   via the M6 pipeline) — PostgreSQL + Web API + Blazor SSR + self-hosted OpenIddict auth:
   translators import the CLI export, edit with review workflow, and export `polish.txt` back
   for patching.

**Architectural identity:** every TMS pattern is lifted **1:1 from TheKittySaver**
(`~/RiderProjects/TheKittySaver` — the canonical reference for Vertical Slice Architecture, DDD
domain, Result monad, the OpenIddict auth server, Docker/compose, testing discipline), with
**one repo-wide deviation: NO MEDIATOR (ADR-0001)**. KittySaver uses `Mediator.SourceGenerator`;
every lifted slice is de-mediatorized on entry (recipe: `docs/kittysaver-lift-map.md`). `Mediator`/`MediatR` packages are
forbidden — never add them back.

## Project status — deployed, pre-launch (no real users yet)

Active development. **Done:** M1 (patcher, empirically proven), **M2 — TMS backend** (all slices
incl. CLI auto-download M2-20; the forum watcher M2-18 / #85 was **deliberately cut to post-MVP**
— owner decision 2026-06, recorded in ADR-0030; game-version registration stays manual, and
neither the loop nor a contributor should pick #85 up), **M3 — Blazor SSR frontend** (manual
QA pass QA-FE / #275 still open), **M6 — deployment** (CD over ssh to a Hetzner VPS + Neon,
staging + prod — ADRs 0008–0029, hosting moved off Azure by ADR-0034). **Open fronts:** M7
game-content catalog (epic #362, spec 0008
agreed, not started), LEGAL/GDPR pack (epic #459 — 01/02/03 landed, incl. the two-phase account
deletion of ADR-0031; 04–07 open), QA-FE manual pass (#275), M4 desktop player app (Avalonia —
ADR-0033), post-MVP TP
backlog (epic #377).

No real users yet, so **API/code breaking changes are free** — no back-compat shims, no
deprecation windows. The one exception is the **database schema**: the stack is deployed with
zero-downtime CD, so migrations follow ADR-0023 (forward-only, N-1 backward-compatible,
expand→backfill→contract) regardless of user count. Live backlog: `gh issue list`; where an
issue body conflicts with this file, **this file wins** — align the ticket before coding.

## Architecture — two bounded contexts, one file contract

```
src/
  Patcher/LotroKoniecDev.{Primitives,Domain,Application,Infrastructure,Cli}               ← PATCHER (stable)
  SharedKernel/LotroKoniecDev.SharedKernel                                                ← TMS-side building blocks (lifted)
  TranslationSystem/LotroKoniecDev.TranslationSystem.{Primitives,Domain,ReadModels,ReadModels.EntityFramework,Projections,Persistence,Contracts,API}
  AuthSystem/LotroKoniecDev.AuthSystem.{API,Domain,Infrastructure,Persistence,Contracts}  ← self-hosted OpenIddict (lifted)
  Frontend/LotroKoniecDev.Frontend                                                        ← Blazor Static SSR, OIDC RP
  Utilities/LotroKoniecDev.{Hateoas,Hateoas.Abstractions,Logging,Options}
```

(`TranslationSystem.Projections` is the in-house precomputed-translation-file store behind the
distribution endpoint — not part of the KittySaver lift map in `docs/kittysaver-lift-map.md`.)

**The contexts share a data contract, not code: the `||` translation file.** CLI `export` →
`exported.txt` → TMS import; TMS export → `polish.txt` → CLI `patch`. Each context owns its own
parser/serializer; **golden fixture files + round-trip tests on both sides** guard against format
drift, and the format itself changes only via ADR. The TMS never references `datexport.dll`/DAT
code (it runs in Linux containers — Docker Compose on a Hetzner VPS in prod); the patcher never touches the
DB (it runs on a Windows gaming box). Distribution is HTTP, not integration: the CLI launch flow
auto-downloads the current translation file from the TMS API (ETag-cached; M2-20), and the
Avalonia app (M4) is a GUI over the same patcher handlers + download.

### Patcher — stable (shipped & empirically proven)

Strict Clean Architecture; dependency rule: **Cli / Infrastructure → Application → Domain →
Primitives**.

| Project | Role |
|---|---|
| `LotroKoniecDev.Cli` | Spectre.Console commands; resolves paths, reports, maps `Error` → exit code |
| `LotroKoniecDev.Application` | feature slices (`Features/<Area>/`): command/query records + slim handlers + services; `Abstractions/` ports incl. in-house `Messaging/` interfaces |
| `LotroKoniecDev.Domain` | `Result`/`Maybe` monads, `Error` + `DomainErrors`, DAT models (`SubFile`, `Fragment`, `Translation`), `VarLenEncoder` |
| `LotroKoniecDev.Infrastructure` | native interop (`datexport.dll`, x86 Windows), DAT handler, forum fetcher, launcher |
| `LotroKoniecDev.Primitives` | constants + enums, zero dependencies |
| `tests/LotroKoniecDev.Tests.{Unit,Infrastructure,E2E}` | patcher tests (E2E Windows-only via `SkippableFact`) |

**Stable means:** the patcher is shipped and empirically proven, so the bar for touching it is
high — but it is **no longer frozen** (ADR-0002 amendment, 2026-06-25). Refactors, renames and
restructuring are allowed when they earn their keep (the `src/Patcher/` grouping was the first);
**any change must keep every existing test green without touching its assertions**, and behavior
proven in `docs/knowledge-base/` must not regress. The TMS still deliberately duplicates the few
tiny building blocks it needs (Result/Maybe/Error shapes, messaging interfaces — they arrive
inside the lifted SharedKernel); consolidating that duplication is an opt-in cleanup, not a
mandate. The DAT/`||` file format still changes only via ADR + updated golden fixtures.

### TMS — lifted 1:1 from TheKittySaver

The lift is **done** — every TMS project mirrors its `~/RiderProjects/TheKittySaver` original. The
per-project map and the **de-mediatorization recipe** (record → in-house `ICommand`/`IQuery`,
explicit closed-handler DI registration, validation and logging inside the handler) live in
**`docs/kittysaver-lift-map.md`**: a **new** TMS slice mirrors the nearest existing sibling slice
in this repo first and reads that map only when no sibling fits.

**Deliberate non-lifts (YAGNI — revisit only on a real, present need):** `Calculators`, domain
events (KittySaver dispatches them via Mediator notifications; the TMS core loop doesn't need
them — if a need appears, design an in-house dispatcher via ADR first). `ReadModels(+EF)` and
per-system `Primitives` were on this list and are now lifted from day 1 (ADR-0002 amendment
2026-06-12).

## Source of truth — the wiki outranks specs, ADRs and code

The GitHub wiki (`koniecdev/LotroKoniecDev.wiki`, cloned **beside** the repo, never inside it) is
where the owner writes down how the product is meant to behave. It is the **highest authority in
the project**: above `docs/specs/`, above `docs/adr/`, above the code. The code-wins rule in the
preamble settles a repo doc against the code — it does not outrank the wiki.

The wiki is also the only source a manual tester has. Testers write and run QA tickets from it,
with no repo access, so a wiki that disagrees with the product does not produce a stale document —
it produces false bug reports that someone pays to close.

**When the wiki and the product disagree, never fix it silently and never "just align the wiki".**
The order is fixed:

1. **Open a ticket for the inconsistency.** State what the wiki says, what the product does, and
   which of the two is asserted where (file, ADR section, wiki line). Do not decide it yourself:
   the wiki holds business intent and only the owner rules on intent.
2. **The verdict lands in the wiki first.** Whatever the owner decides, the wiki is updated to say
   it plainly. Until that edit exists, the question is not settled, and nothing downstream may be
   built on the answer.
3. **Then open a second ticket to bring code, specs and ADRs in line with the corrected wiki.**
   That ticket cites the wiki as its authority. An ADR that now contradicts the wiki is amended or
   superseded like any other reversed decision — the ADR does not win because it was written first.

Worked example (2026-08-22): the wiki says a LOTRO version has one to three dot-separated segments;
`LotroNotationVersion` accepts any count, and ADR-0003 §3 records that as deliberate ("no maximum
segment count is imposed"). A tester writing a ticket from the wiki hit the gap. The resolution is
**not** to soften the wiki sentence to match the validator — it is the owner ruling on the domain
rule, the wiki stating it, and a follow-up ticket amending ADR-0003 and the validator.

## Read-first routing (do this BEFORE touching the area)

| You're about to… | Read first |
|---|---|
| Build/change a **TMS slice** | the nearest existing sibling slice in `TranslationSystem.API/Features/…`; no fitting sibling → the KittySaver original (`AdoptionSystem.API/Features/…`) + the lift map and de-mediatorization recipe in `docs/kittysaver-lift-map.md` |
| Work a GitHub ticket end-to-end | run **`/ticket <number>`** (mind the this-file-wins rule in Project status) |
| Triage a pile of tickets before working them (premise check, no implementation) | run **`/preflight <numbers…>`** — wiki-first READY/CLARIFY/BOUNCE verdict comment + lane per ticket; then fire `/ticket` per READY one, each in a fresh session |
| Make a PR body, report or comment readable for a non-native reader | run **`/b2-english <PR# / comment URL / file>`** — `/ticket` and `/work-ticket` run it as their last step |
| Touch the `\|\|` translation file (parser, serializer, a column) | `README.md` → "Translation file format" for the format, the rules digest below, ADRs 0039/0042/0043/0047 for the reasoning; golden fixtures on both sides; the format changes only via ADR |
| **File** an issue, label one, or title one | `docs/labels.md` — the five axes (`priority-*`, `type-*`, `severity-*`, `area-*`, process), the title convention and the three-signal epic rule, **shared 1:1 with TheKittySaver**; a change in one repo is ported to the other in the same session. Read it *before* `gh issue create`, not after |
| Run the backlog autonomously (Loop mode) | **`/backlog`** → `scripts/claude/backlog-loop.sh` — one fresh headless session per ticket; manual: `docs/claude-loop.md` |
| Touch DAT binary parsing / writing / native interop | delegate to the **`dat-format-expert`** agent |
| Re-investigate update behavior, vnum, translation survival, launch flow | **don't** — empirically settled in `docs/knowledge-base/` (start at its README) |
| Make a non-trivial architectural/modeling decision | skim `docs/adr/`, then **write a new ADR** (`/adr`); anchors: 0001 (no mediator), 0002 (TMS pivot + freeze/unfreeze amendments), 0008 (cloud-agnostic deployment + env strategy — M6), 0009 (browser E2E via Testcontainers + Playwright) |
| Deploy/operate the stack, or set env vars per environment | `docs/deployment/runbook.md` — env-var matrix (service × environment), secret generation, the issuer/redirect/authority/CORS gotchas, bring-up sequence + DB migrations. Ingress/routing shape: ADR-0034 (Caddy) + **ADR-0041** (why there is no gateway behind it) |
| Add a proxy/gateway, expose a new service, or wire a client to an API path | **ADR-0041** — there is no API gateway: Caddy owns transport, the discovery document owns semantics, the frontend BFF owns aggregation. Clients resolve endpoints by **rel name** (ADR-0040's vocabulary), never by hardcoded path |
| Touch the update lifecycle (GameVersion, import diff, invalidation, distribution, CLI sync) | `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md` — the agreed domain spec |
| Touch update-day behavior on the client (sentinel, orchestrator, any new DAT write path) | `docs/specs/0012-update-resilience.md` (Tier 0/1 rules as amended 2026-08-17) + **ADR-0047** — every write goes through the per-row source guard; the no-masking invariant has no path exceptions |
| Touch the game-content catalog (CatalogEntry/TextSlot, Companion zip import, catalog browser, memberships) | `docs/specs/0008-game-content-catalog-layer.md` (agreed) + `docs/knowledge-base/lotro-companion-data-model.md` (the verified `key:<FileId>:<GossipId>` join — never join on text). Naming rule: **never "entity"** in this layer (DDD-Entity misconception) |
| Implement a feature whose business rules are fuzzy | **`/spec`** first (seed → questions → agreed spec in `docs/specs/`) |
| Write or hand over a **manual QA ticket** (external tester) | **`/qa-ticket`** — every scenario verified against HEAD first; `/qa-ticket #<n>` re-baselines an existing one |
| Review a finished change | the **`code-reviewer`** agent |
| Understand the backlog / milestones | `gh issue list` + Roadmap digest below |
| Compare with the Russian sister project | `docs/RUSSIAN_PROJECT_RESEARCH.md` + `docs/knowledge-base/russian-project.md` |

## Commands

```bash
# Build — zero-warnings gate: TreatWarningsAsErrors is on repo-wide; any warning IS a failing build
dotnet build LotroKoniecDev.slnx

# Tests
# The unfiltered run is the gate before EVERY push — the filtered ones are for iterating only.
dotnet test                                            # everything runnable on this OS
dotnet test tests/LotroKoniecDev.Tests.Unit            # fast, pure unit (must always be green)
dotnet test tests/LotroKoniecDev.Tests.E2E             # full pipeline — auto-skips off-Windows
dotnet test --filter "FullyQualifiedName~Fragment"     # filter by name

# Run the CLI (Windows; needs LOTRO. `export` reads and needs NO admin — #629; only DAT writes do)
dotnet run --project src/Patcher/LotroKoniecDev.Cli -- export                 # DAT → data/exported.txt
dotnet run --project src/Patcher/LotroKoniecDev.Cli -- patch polish           # translations/polish.txt → DAT
dotnet run --project src/Patcher/LotroKoniecDev.Cli -- launch polish          # hash-check → patch if changed → launch
# or the .bat wrappers: export.bat (no elevation) / patch.bat, lotro.bat (self-elevate)

# GitHub tickets (BRD/spec-driven flow)
gh issue list --state open                             # backlog; titles follow "M{milestone}-{nn}: Title"
gh issue view <n>                                      # body holds Context / Depends on / Tasks / Acceptance criteria
gh issue develop <n> --checkout                        # create + checkout the linked "{n}-{kebab-title}" branch
gh pr create --fill --body "Closes #<n>"               # PR title mirrors the ticket; body closes it

# Autonomous backlog loop (Loop mode) — bash conductor + one FRESH headless session per ticket
scripts/claude/backlog-loop.sh                         # drain every ready ticket, serially
scripts/claude/backlog-loop.sh -n 3                    # at most 3 tickets
scripts/claude/backlog-loop.sh 123 130                 # exactly these tickets, in order
caffeinate -is scripts/claude/backlog-loop.sh          # overnight run on macOS (blocks sleep)
scripts/claude/next-ticket.sh                          # print the next READY ticket (priority + deps)
scripts/claude/work-ticket.sh 123                      # one ticket, one fresh headless session
# defaults: model + effort from ~/.claude/model-policy.env (Opus 5.5 · xhigh since 2026-09-22), else opus · high · permission-mode auto — override via LOOP_MODEL /
# LOOP_EFFORT / LOOP_PERMISSION_MODE / LOOP_UNSAFE=1 · full manual: docs/claude-loop.md

# TMS — EF Core migrations (write context owns them; --connection makes it work without appsettings/live DB)
# dotnet-ef is a pinned local tool (dotnet tool restore). No --startup-project: it would equal
# --project, and dotnet-ef 10.0.9 mis-parses the pair when both carry the identical value.
dotnet ef migrations add <Name> \
  --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.Persistence \
  --context ApplicationWriteDbContext \
  -- --connection "Host=localhost;Database=lotro_translation;Username=postgres;Password=changeme"

# TMS dev — INFRA-ONLY compose (postgres + migrator + mailpit + aspire) + THREE host Kestrels (ADR-0006, amended #190/M6-14)
docker compose up -d                                   # boots infra + runs the one-shot migrator; NO app images (the apps run on host)
docker compose up --build migrator                     # rebuild the migrator image only after adding an EF migration
docker compose logs -f migrator                        # watch the one-shot schema migration (TMS + Auth contexts)
docker compose down                                    # stop; add -v to also drop the postgres volume (fresh DB)
# scripts/up.sh | up.ps1 = recommended boot — bootstraps .env from .env.example, then `docker compose up` (no cert, no API build).
# One-time host prereq so the host Kestrels serve HTTPS:  dotnet dev-certs https --trust
# The three apps run on the HOST (hot reload, breakpoints, no image rebuild) — all three at once via the Rider
# compound ".run/TMS dev (all hosts)", or each via its own `dotnet run` (each uses its `https` launchSettings profile):
dotnet run --project src/AuthSystem/LotroKoniecDev.AuthSystem.API                 # auth-api → https://localhost:5003
dotnet run --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.API   # tms-api  → https://localhost:5002
dotnet run --project src/Frontend/LotroKoniecDev.Frontend                         # frontend → https://localhost:7017
# Endpoints (HTTPS): tms-api :5002 · auth-api :5003 · frontend :7017 · aspire :18888 · mailpit :8025
#   (e.g. curl -k https://localhost:5002/health). The browser, the host RP and the host resource server all resolve
#   localhost:5003/:5002 identically, so one OIDC Authority/Issuer serves every leg + the token `iss`. tms falls back
#   from Auth:Authority to Auth:Issuer (https://localhost:5003) to reach the host auth Kestrel; no config differs by run mode.

# TMS — Production-PARITY stack (compose.prod.yaml; ADR-0008 §4 / M6-07) — SEPARATE from dev compose.
# ALL FOUR images + a Caddy reverse proxy run under ASPNETCORE_ENVIRONMENT=Production: real OpenIddict
# keys, DP keyring volumes (auth + frontend), self-hosted Postgres over SSL, the containerized Frontend.
# Catches prod-only breakage on a laptop before staging. Coexists with the dev stack (separate project).
scripts/up-prod.sh | up-prod.ps1                       # recommended boot — bootstraps .env.prod (with generated
                                                       #   OpenIddict secrets) + local CA/proxy/Postgres certs, maps the
                                                       #   *.lotro.test vhosts to loopback (idempotent; admin only the
                                                       #   first time), then up. Args pass through (e.g. --build, -d).
docker compose -f compose.prod.yaml --env-file .env.prod up --build      # raw command (after the bootstraps + a manual hosts entry)
docker compose -f compose.prod.yaml --env-file .env.prod --profile local-smtp --profile local-otel up  # + mailpit + aspire (all-local; needed for a green deep auth /health)
docker compose -f compose.prod.yaml --env-file .env.prod down            # add -v to drop prod volumes (fresh DB/keys)
# up-prod.{sh,ps1} runs both one-time bootstraps for you: gen-openiddict-keys (3 secrets → .env.prod) +
#   init-prod-https (local CA → .docker/prod-https/). It also auto-maps the hosts file (cross-platform:
#   sudo on macOS/Linux, UAC on Windows); manual equivalent: 127.0.0.1 app.lotro.test auth.lotro.test tms.lotro.test
# Browser OIDC login: https://app.lotro.test. Health (trust the local CA):
#   curl --cacert .docker/prod-https/rootCA.crt https://auth.lotro.test/health/ready
```

The dev stack is **infra-only** (ADR-0006, amended by #190 / M6-14): `compose.yaml` runs postgres +
migrator + mailpit + aspire-dashboard, and the **three apps run on the host** as the canonical dev loop —
auth-api (`https://localhost:5003`), tms-api (`https://localhost:5002`), frontend (`https://localhost:7017`),
each via its `https` `launchSettings` profile (`dotnet run`, or the Rider compound `.run/TMS dev (all hosts)`).
Host Kestrels serve HTTPS with the **native** ASP.NET Core dev cert (one-time `dotnet dev-certs https --trust`)
— no PFX, no mount. Because the browser, the host RP and the host resource server all resolve
`localhost:5003`/`:5002` identically, a single OIDC `Authority`/`Issuer` serves every leg and the token `iss`
matches; tms-api's back-channel uses the `Auth:Authority`→`Auth:Issuer` fallback (`AuthSettings.EffectiveAuthority`)
to reach the host auth Kestrel. The containerized `auth-api`/`tms-api` services were retired from dev because they
were neither the fast inner loop (host Kestrels give hot reload + breakpoints + no image rebuild) nor prod-parity —
`compose.prod.yaml` is the sole containerized/parity stack and exercises the very same Dockerfiles. The migrator is
a one-shot container (TMS migrates through its Persistence project, Auth through its API — only those carry EF Core
Design); it runs to completion against both DBs so the host Kestrels hit a migrated schema. Dev uses **ephemeral**
OpenIddict keys; production-like runs supply real keys via env (see `.env.example`).

`compose.prod.yaml` is the **separate production-parity stack** (ADR-0008 §4 / M6-07; the dev
`compose.yaml` is left untouched). It runs all four images **plus a Caddy reverse proxy** under
`ASPNETCORE_ENVIRONMENT=Production`, reproducing the cloud topology locally so prod-only breakage
surfaces before staging. Caddy terminates TLS on one origin per app — `app|auth|tms.lotro.test` —
reachable **identically** from the browser (hosts file — `.test` is not auto-resolved) and from the in-stack Frontend
container (Caddy network aliases). That single shared origin is what lets one OIDC `Authority` serve
both the browser front-channel and the Frontend's back-channel, so the containerized-RP two-legs
problem of ADR-0006 dissolves behind the proxy (this folds in the M6-08 ingress). The proxy forwards
`X-Forwarded-Proto/Host/For`, exercising `UseForwardedHeaders` (M6-02). Production specifics: real
OpenIddict keys (`scripts/gen-openiddict-keys.{sh,ps1}`), DP keyring **volumes** for auth + frontend
(M6-04 / ADR-0005), self-hosted Postgres with `ssl=on` (`Ssl Mode=Require;Trust Server Certificate=true`;
swap to a managed DB = change just the two `ConnectionStrings__*` in `.env.prod`), real SMTP + OTLP from
env (mailpit/aspire only behind `--profile local-smtp|local-otel`). In Production OpenIddict rejects
plain-HTTP requests, so **both** tms-api (OIDC metadata + JWKS for token validation) and the Frontend
(OIDC discovery/token/userinfo) reach auth **through the proxy** over `https://auth.lotro.test` —
`Auth:Authority` / `AuthSystem:Authority` is that proxy origin (matching the token `iss`), not the
in-network `http://auth-api:8080`. .NET validates the proxy's leaf cert against the **OS trust store**
(it ignores `SSL_CERT_FILE`), so a shared mount-only entrypoint (`.docker/trust-ca-entrypoint.sh`)
installs the local CA via `update-ca-certificates` and drops back to the non-root app user — no app or
Dockerfile change (real prod uses a publicly-trusted ingress cert, so the shim is parity-stack-only).
Secrets live in the git-ignored `.env.prod`; TLS material in `.docker/prod-https/` (also git-ignored),
both bootstrapped by `scripts/up-prod.{sh,ps1}`.

Exit codes (CLI): `0` success, `1` invalid arguments (incl. `ErrorType.Validation`), `2` file not
found, `3` operation failed, `4` cancelled.

## DAT binary format

The SubFile / Fragment / VarLen layout is pre-catalogued in the **`dat-format-expert`** agent (the
routing row above — every DAT parse, serialize or interop change goes through it) and lives in the
patcher's Domain parsers; the empirical findings (chunk patching, survival, vnum) are in
`docs/knowledge-base/` (start at its README). Texts are SubFiles with FileId high byte `0x25`.

## Translation file format — THE inter-context contract

Format, columns and worked examples: `README.md` → "Translation file format" (the one copy;
`<--DO_NOT_TOUCH!-->` is the argument placeholder). Each context owns its own parser/serializer and
**golden fixtures + round-trip tests on both sides** pin the contract; results are sorted by FileId
then GossipId for sequential DAT I/O. The rules below stay in force — each ADR holds the reasoning:

- `args_order` / `args_id`: `NULL` or `1-2-3` (1-indexed in file, 0-indexed internally); anything
  else **rejects the row and is reported** (ADR-0042) — a CLI patch warning, a failed TMS upload;
  whether the positions fit is checked downstream in `Fragment.TryReorderArgRefs`.
- **The line is CARVED, never `Split`** (ADR-0042, #597): `||` inside content and a trailing run of
  `|` are legal; each context's `TranslationLineCarver` scans forward for the id separators and
  backward for the tail (three or four, sniffed from the last field). Never reintroduce `Split`.
- **Content escape `\`→`\\`, CR→`\r`, LF→`\n`, injective** (ADR-0039): every writer escapes, every
  reader unescapes, via each context's `TranslationLineEscaper`; text held anywhere else is RAW.
- **Content ≤ 32767 UTF-16 code units — bounded by the DAT, not the file** (ADR-0043): refused at
  the TMS API (`UpsertTranslation.Validator` + a `CHECK` constraint, never a `varchar(n)` narrowing),
  warn-skipped by the patcher before any write (`Fragment.IsWritablePiece`); `Fragment.Write` still
  throws as the last resort.
- **Stale Polish never lands over changed English — an INVARIANT enforced per row at write time**
  (ADR-0047, #659): the 7th column `source_digest` (16 hex of the framed SHA-256 `SourceHash`) must
  match the fragment's English or what the patcher last wrote there (`<file>.ledger`), whatever path
  writes the DAT; otherwise the row is skipped and reported as `source moved`. No operator override;
  a six-column file is not patchable; digest parity is a golden fixture pinned on both sides.
- **Changing this format requires an ADR + updated golden fixtures in BOTH contexts.**

## Game update behavior — empirically settled, do not re-test

`docs/knowledge-base/` (README index) holds the proof: the **forum version** is the reliable game
version (DAT vnum 112/3 is dead as a content signal), the launcher patches **chunk-based** so
translations survive updates per-SubFile (9 live tests incl. two majors; a modified SubFile reverts
its fragments — repair is the normal re-patch / the spec-0001 invalidation loop), and the simplified
hash-check → patch → launch flow is validated. Re-investigating any of it is a BOUNCE.

## Project house rules

- **Zero warnings.** `TreatWarningsAsErrors` is repo-wide. Fix it; don't suppress it (a scoped
  `.editorconfig` exception requires a stated reason, like the `Result._value` guarded getter).
- **Errors are values, not exceptions.** Business failures → `Result.Failure(Error)` via
  `DomainErrors.*` factories / `Error.Validation(...)`. Guards (`Ensure`,
  `ArgumentNullException.ThrowIfNull`) are for **programmer** errors only. The API's
  `ExceptionHandlers/` are safety nets, not a control-flow mechanism.
- **No mediator — slim SRP handlers (ADR-0001), repo-wide.** One use case = one record + one
  handler implementing the in-house `ICommandHandler<,>`/`IQueryHandler<,>`. Consumers inject the
  closed handler interface directly. Lifted KittySaver code is de-mediatorized on entry.
- **DI validation is always on (#572).** Every composition root enables `ValidateScopes` +
  `ValidateOnBuild` in ALL environments (`builder.Host.UseDefaultServiceProvider` on the three web
  hosts; `ServiceProviderOptions` in the CLI `TypeRegistrar`) — a new host copies the pattern. It
  validates registered services only, so endpoint integration tests stay the guard for a forgotten
  closed handler registration. **In the CLI, Spectre command types are registered `AddScoped` and
  resolved through a single process-lifetime scope in `TypeResolver`** — they inject scoped
  handlers, so the old `AddSingleton` + root-provider resolve is a captive dependency that refuses
  to build. A new CLI command follows suit. Nothing in CI covers that graph (the CLI is
  `net10.0-windows/win-x86`), so changes to it need a Windows `export`/`patch`/`launch` smoke.
- **CQRS read/write split, day 1 (ADR-0002 amendment).** Query handlers read POCO `ReadModels`
  through `IApplicationReadDbContext`; command handlers load and mutate aggregates via
  repositories + `IUnitOfWork`. The write model never serves list/search queries; every new
  aggregate ships with its read model + EF configuration in the same change.
- **Patcher is stable, not frozen** (ADR-0002 amendment 2026-06-25) — refactor it when it earns
  its keep, but every change must keep its existing tests green (assertions untouched) and must not
  regress behavior proven in `docs/knowledge-base/`.
- **TMS ships with auth from day 1.** Endpoints are authorized by default (public ones are
  explicit) and edited rows carry user attribution: `Translation.SubmittedById` is stamped on
  upsert (added in M2-11; persisted via the `IdentityId` converter in `TranslationSystem.Persistence`),
  and `ApprovedById` lands with the approve slice (#101 / M2-12). No auth-less interim state to
  retrofit later.
- **Hypermedia links are authorization-aware, not role-branched (ADR-0040).** `ILinkFactory` replays
  the *target* endpoint's own policy (`IAllowAnonymous` → `AuthorizationPolicy.CombineAsync` incl.
  the fallback → `IAuthorizationService`) before emitting a link, so a rel is never advertised to a
  caller who would get 401/403 following it. Never restate a role rule inside a link factory — a
  link factory encodes *state* rules only (removed, `Draft`/`NeedsReview`, `Unprocessed`). The TMS
  service document (`GET /`) is the one deliberate `AllowAnonymous` hole in authorized-by-default:
  it advertises only what the caller may already reach, and it is what lets the CLI (and M4) boot
  without hardcoded paths. Only parameterless entry points belong in it; id-keyed affordances
  (`approve`, `delete`, `import`) live on the representation carrying the id.
- **No API gateway — the discovery document IS the client contract surface (ADR-0041).** Caddy owns
  transport, each API's discovery root owns semantics, the SSR frontend owns aggregation; nothing
  goes in the request path between them. So **rel names are a frozen public contract** — additions
  are cheap, renames break every client — and a client takes one root URL per service as config and
  resolves everything else by rel (#610 frontend, #611 CLI). A service split does **not** justify a
  gateway: the departing service hosts its own root and the one it left links to it with a single
  configured absolute URI. Reopening triggers are listed in the ADR.
- **Validation:** FluentValidation **for commands only** — the command handler injects
  `IValidator<TCommand>` and maps failures to `Result` (never throws). Queries validate inline
  in their handler. Every validator must be registered in DI.
- **Handlers are orchestrators.** Business logic lives in domain/application services; handlers
  validate, delegate, return.
- **No primitive obsession in the domain layer.** Every domain concept that carries a constraint
  or identity is a `ValueObject` — never a raw `string`, `int`, `Guid`, etc. passed or stored
  directly. Golden templates:
  `TranslationSystem.Domain/Aggregates/GameVersionAggregate/ValueObjects/LotroNotationVersion.cs`
  (constrained-string VO) and
  `TranslationSystem.Domain/Aggregates/GameVersionAggregate/Entities/GameVersion.cs`
  (an aggregate that models its constrained version concept as a VO — its timestamp and enum
  stay primitive, because they carry no extra invariant).
- **EF Core (`TranslationSystem.Persistence`):** Fluent API only (never attributes), `nameof()`
  for column names, `MaxLength`/`Precision`+`Scale` over `HasColumnType`, no needless
  `IsRequired()` (value types & non-null strings are already required), FK property names
  parametrized with `nameof()`.
- **VO persistence mapping — `ComplexProperty` by default, `OwnsOne` when index is needed.**
  `ComplexProperty` is the semantically correct mapping for VOs (pure value type, no identity
  tracking). Switch to `OwnsOne` only when the property requires a DB index — `ComplexProperty`
  cannot be indexed in EF Core 10 (limitation removed in EF Core 11). With `OwnsOne`, define
  `HasColumnName` explicitly and put `HasIndex` inside the owned builder.
- **Migrations are forward-only and N-1 backward-compatible (ADR-0023).** The deploy gate commits
  the schema **before** traffic moves, and rollback reverts code, never schema — so the
  currently-running app revision must survive every migration. Never rely on `Down()` outside
  local dev; recovery is roll-forward or a Neon restore — PITR or the MIGR-04 pre-migration
  auto-snapshot branch (runbook). Destructive operations
  (drop/rename a column or table, change a type, add `NOT NULL`, tighten a constraint / unique
  index over existing data) ship as **expand → backfill → contract across ≥ 2 deploys**; a
  deliberate destructive step carries an in-file `MIGRATION-SAFETY: acknowledged — <reason>`
  comment (CI gate: #338 / MIGR-03). Migration-touching PRs additionally run the executable
  N-1 proof: the previous release's integration suites against the HEAD schema
  (`n1-compat.yml` / `scripts/n1-compat.sh` — ADR-0024; the factories' seam is
  `N1_COMPAT_SCHEMA_SCRIPTS_DIR`, inert in normal test runs).
- **Right-size the design — YAGNI by default.** Before proposing an abstraction, cache, config
  knob, queue, or new infra, check it solves a **real, present** need from the current
  spec/ticket — not a hypothetical future. Pick the simple path and note the trade-off in one line.
- **Agent fan-out is budgeted; agents inherit the session's model and effort** (the repo pins no
  model tier of its own — see the 2026-08-14 prune; the churn it ended: Fable switch 2026-07-09→11,
  same-day Opus revert #497, Fable re-enable #503, Opus for reviews 2026-07-13, Fable again
  2026-07-17, Opus everywhere 2026-08-05). Hard cap:
  **max 4 subagents in parallel**, no chained waves by default; a small diff gets reviewed
  **inline, zero agents**. The committed `code-reviewer` agent carries **`model: inherit`** — it
  runs on whatever model and effort the session runs on, so a fresh clone reviews with the
  contributor's own tier instead of a model this repo has no business pinning for them;
  `/code-review` and `/security-review` follow the session model/effort the same way; loop-mode
  worker sessions take model and effort from the maintainer policy (Opus 5.5 at **xhigh** since
  2026-09-22; the script fallback without it is **effort high**, the `LOOP_EFFORT` default) — unless the prompt for that run
  explicitly says otherwise. Applies to interactive sessions and loop-mode workers alike. The
  concrete values live in agent frontmatter and in the loop scripts' fallback defaults; the
  maintainer pins their own tier machine-locally (a central model policy outside the repo), which
  is deliberately **not** something a clone inherits. If this prose and the frontmatter ever
  disagree, the frontmatter wins.
- **Token discipline.** Measured 2026-09-12 over 30 days across the maintainer's account: ~54% of
  spend is cache reads — context length × turn count — and this repo boots the largest context of
  the three projects (median first-turn cache write ~84k tokens vs ~58k in TheKittySaver), so the
  levers are fewer turns and shorter contexts, not prose golf. Batch independent tool calls into
  one message; chain dependent shell steps with `&&` when only the final result gates (build + the
  whole suite = one call). One ticket = one session: `/clear` after the PR — a second ticket in
  the same context pays the first one's whole conversation as cache reads on every turn.
  Questions to the user belong in `/ticket`'s step-2 gate and nowhere later: the prompt cache dies
  after 1h idle, so a parked fat context re-primes at full price — deliver on documented
  assumptions and fix from the Ticket report instead. Opus 5.5 and Fable run a native 1M window in
  Claude Code and auto-compaction stays at its ~967k default on purpose (never set
  `autoCompactWindow`, `CLAUDE_CODE_AUTO_COMPACT_WINDOW` or `CLAUDE_CODE_DISABLE_1M_CONTEXT` —
  compaction is not wanted here), so only you cap a session: past ~200k in `/context` a ticket
  session is a marathon — finish, report, `/clear`. Pick model and effort before the first prompt:
  `/model` mid-session is a full cache miss on every model, `/effort` on every model except
  Fable 5.1 (unverified on Opus 5.5 — assume a miss). Back-to-back sessions reuse only the
  tool layer of the cache (the rest re-primes with the git snapshot) — batch them for focus, not for cache.
- **Frontend is Static SSR — enforced, not just documented.** No WebAssembly, no SignalR circuit,
  no per-user server state; forms post via `<form method="post" @formname @onsubmit>` (the SSR
  `@onsubmit` special-case) or `<EditForm OnValidSubmit>` — never interactive `@on*` handlers,
  `@rendermode`, `StateHasChanged`, or `AddInteractive*`. **No inline `<script>` either (#670):**
  the Frontend's CSP sends `script-src 'self'`, so an inline script is dead on arrival and only the
  browser console reports it — put the code in a file under `wwwroot` and load it with `src=`, or, if
  one truly must be inline, add a nonce in `SecurityHeadersMiddleware`, never `'unsafe-inline'`. That
  is also why `App.razor` carries no import-map component: it renders an inline script and a Static
  SSR app never resolves a module specifier. `scripts/check-ssr-purity.sh` (with a `.ps1` twin for
  local Windows devs) gates all of it in **both** `pr-verify` and `ci`, before `setup-dotnet`, and
  `scripts/tests/check-ssr-purity.tests.sh` proves the guard still fires. Deploy-time backstop: smoke
  leg 2 fails if a deployed page serves an inline script its own CSP blocks. Genuinely need
  interactivity, or an inline script? Both are ADR-first architecture changes.
- **Auth Razor Pages: never put a caller-supplied value in an `asp-route-*` attribute (#681, #682).**
  Razor writes a tag-helper attribute through `WriteLiteral` with the encoder switched off, and
  `RazorPageBase.WriteLiteral` is the page-output sink CodeQL's `cs/web/xss` recognises — so
  `asp-route-returnUrl="@Model.ReturnUrl"` raised two high alerts that sat open on `main` and blocked
  every merge (`scripts/claude/work-ticket.sh` refuses a PR with open alerts). The tag helper encodes
  the value before the browser sees it, so it was never exploitable — but "not exploitable" does not
  clear an alert, and neither does a sanitizer that returns its input (the failed attempt in #536).
  Use a plain attribute: `<input type="hidden" name="returnUrl" value="@Model.ReturnUrl" />` compiles
  to `WriteAttributeValue`, is HTML-encoded, and is not a sink. A `<form>` with no `asp-*` attribute
  still emits its antiforgery token and posts to the current URL, and handler binding reads the form
  field before the query string. `LocalReturnUrl.Sanitize` only answers "is this target local"; the
  encoding at each print site is what keeps these pages safe. (`Html.Raw` is a sink of its own.)
- **The auth origin sends its own CSP, so an auth page's inline `<style>` needs the nonce (#693).**
  `AuthSystem.API/Middleware/SecurityHeadersMiddleware` adds CSP, `X-Frame-Options: DENY`, nosniff
  and `Referrer-Policy: no-referrer` to every response outside Development. Each account page keeps
  its styles inline, so it writes `<style nonce="@CspNonce.Get(HttpContext)">`; a `<style>` without
  it is dropped by the browser and the page loads unstyled. `script-src` is `'self'` with no nonce,
  so page script goes in a file under `wwwroot` (`login.js`). `form-action` lists the web client's
  origins, because the login POST ends in a redirect to the frontend's `/callback` and Chrome checks
  every redirect of a form. That also makes the frontend's `ResponseMode.Query` load-bearing:
  OpenIddict's form_post page submits itself with an inline script this CSP blocks. The guards:
  `check-ssr-purity` scans `src/AuthSystem/**/*.cshtml` for both rules, smoke leg 6 checks the
  deployed pages, the integration suite checks every Razor page endpoint, and the Frontend E2E suite
  (auth runs in Testing there, with the CSP on) fails on any `securitypolicyviolation`.
- **Auth Razor Pages are rate limited by default; opting out is the explicit act (#692).**
  `app.MapRazorPages().RequireRateLimitingByDefault("auth-page-limit")` gives every account page a limit,
  so a new page is covered without anyone remembering an attribute — five of the ten pages, login and
  register among them, were missed exactly that way. A page that needs a different budget says
  `[EnableRateLimiting]`, one that needs none says `[DisableRateLimiting]`, and the convention leaves
  both alone. **Never replace it with a plain `RequireRateLimiting` on that group:** `GetMetadata`
  returns the last match and a group convention is appended *after* a PageModel attribute, so the group
  would silently eat the page's own policy. On a minimal-API endpoint it is the other way round, because
  there the endpoint's own call runs after its group's — same method, opposite winner. A page budget also
  counts **POSTs only**: a Razor Page is one endpoint for GET and POST, so a send-sized budget gets spent
  on page views and locks the user out of the form (ADR-0046's lesson, now in `forgot-password-limit`
  too). Mail sent to a caller-typed address needs a second budget keyed by **the account**, not the IP
  (`IPasswordResetRequestThrottle`) — an attacker rotating IPs gets a fresh IP budget every time. Key it on
  the **id**, never the address text: Identity's `NormalizeEmail` runs `Normalize()` first, so a Polish
  address written with a combining accent resolves to the same account while a text key would hand the two
  identical-looking spellings a budget each.
- **A per-address rate limit the frontend can reach on the auth API or the TMS API is keyed on the
  visitor, never on the frontend container (ADR-0054, #819, #823).** Every frontend→API call arrives
  through Caddy from the frontend container, so `Connection.RemoteIpAddress` there is one address for
  every user. The TMS API's one policy (`fixed-by-ip`) and the three auth policies that traffic
  reaches (`fixed-by-ip`, `auth-endpoint-limit`, `change-email-limit`) take their key from each API's
  own copy of `RateLimitPartitionKeyResolver`: the visitor's address the frontend forwards in
  `X-LOTRO-Client-Address`, honoured only next to the per-environment `X-LOTRO-Frontend-Key` (SHA-256
  digests, constant-time compare, exactly one value each, the address must parse), otherwise the
  connection's own address. The browser-facing page policies and `register-limit` stay on the
  connection's address on purpose: the frontend never posts to a Razor page nor to `auth/register`,
  and the key must never become a bypass for the login brake or a fresh mail budget per invented
  address. A new outgoing path from the frontend to either API carries
  `FrontendCallerDelegatingHandler` with that API's key (today: the auth API's typed account client,
  token client and OIDC back-channel, and the TMS API's typed client), outside any resilience handler
  so a retry never repeats a header. The header names live once, in `Hateoas.Abstractions`. The key
  is one `FRONTEND_CALLER_KEY` per box `.env`, read by all three services (one key per box, not one
  per API); compose refuses to render without it and every app refuses to boot without it outside
  Development/Testing — so it lands on the staging box before a change that needs it merges.
- **Every user-visible date is Poland time, never raw UTC (#736).** Static SSR has no reader time
  zone: the server's own zone is UTC in a container and the request carries none. The product serves a
  Polish audience, so a stored instant is converted to `Europe/Warsaw` at the moment it is printed —
  page, e-mail or download file name alike — through the context's own extension
  (`Frontend.Infrastructure.Formatting.DateTimeOffsetExtensions`,
  `AuthSystem.API.Extensions.DateTimeOffsetExtensions`). A timestamp that stands alone carries the
  words `czasu polskiego`; a sentence that already names the zone uses the unlabelled overload. UTC
  stays the wire and storage format — the conversion is presentation only, and the runtime images ship
  `tzdata`, so the zone lookup is safe.
- **No client hardcodes an API path — enforced, not just documented (#610 frontend, #611 CLI).**
  There is no gateway (ADR-0041), so every entry point is resolved by **rel name** — the Frontend
  through `IDiscoveryCache.ResolveTranslationSystemHrefAsync(Rels.<Name>)`, the CLI through
  `ITranslationFileEndpointResolver` — and every id-keyed action follows the href the loaded
  resource advertises. A missing rel is a failure (`ProblemDetails` in the Frontend, a `Result`
  error in the CLI) — never a locally composed path, because an absent rel means the server does
  not offer that affordance to this caller. `scripts/check-client-hypermedia.sh` (with a `.ps1`
  twin) flags an API path in any string literal under `src/Frontend/` **and** `src/Patcher/` and
  gates it in **both** `pr-verify` and `ci`, alongside the SSR guard; prose mentions in comments
  stay allowed. The one bounded exception is the editor's detail URI
  (`{discovered translations href}/{id}`) — the `/editor/{id}` route hands over an id, not a link,
  and it is documented as such in `TranslationEditorLoader`.
- **The CLI resolves its download URL from discovery, and degrades without guessing (#611).**
  `SyncTranslationFileCommand` still takes one input, `TmsBaseUrl`; everything else comes from
  `GET {baseUrl}/` with the HATEOAS vendor `Accept` (links are opt-in — a plain-JSON request gets a
  link-less document). Discovery is the primary path and the `.endpoint` sidecar next to
  `polish.txt`/`.etag` is the **outage** fallback only: a server that answers but does not advertise
  `translation-file`, or advertises an href off the configured origin, is a refusal, not an outage —
  no fallback, no composed path. A resolved href is re-validated (absolute, https except loopback,
  same origin as the base URL) whether it came from the wire or from disk. Because the launch must
  never block on the network (spec 0001 Q5), an unresolvable endpoint reports
  `EndpointUnresolvedUsedCache` and lets the launch continue on the local file; with no local file
  the launch path reports it and exits 2, exactly as it does today. Same reasoning downgrades a
  failed `.endpoint` write to a logged warning — that sidecar is a hint, and the next run
  re-resolves it — while a failed `Save` of the downloaded file stays fatal. The TMS adapters use
  their own keyed `HttpClient` with **`AllowAutoRedirect = false`**: the origin check on the
  resolved href is worthless if a 302 can carry the request off it (the redirect target would serve
  both the body and the ETag that hashes it, so the integrity check would confirm the wrong file).
  The forum fetcher keeps redirects — it targets a third-party site.
  **The patcher's one allowed shared reference is `Utilities/LotroKoniecDev.Hateoas.Abstractions`**
  (`MediaTypes.HateoasJson`, and `LinkDto` in tests only). It is not the TMS side, and the vendor
  media type is centralised there precisely so the two ends cannot drift; the CLI still re-types the
  link envelope rather than linking `TranslationSystem.Contracts`, with a parity test standing in
  for the compiler. Note `BoundedContextIsolationTests` covers patcher Primitives/Domain/Application
  only — Infrastructure and Cli are `net10.0-windows`, so a `net10.0` test project cannot reference
  them and nothing mechanically blocks the next reference added there.
- **Docker restore layers are loud and gated (ADR-0028, amended).** Every Dockerfile that lists
  `.csproj` files must COPY the **full transitive closure** of the projects it restores.
  `dotnet restore` treats a missing project file as `Skipping project … because it was not found`
  and still **exits 0**, so a stale list silently caches an incomplete restore layer. Two defenses:
  image builds run `dotnet build`/`publish` with **`--no-restore`**, turning the gap into a hard
  `NETSDK1004`; and `scripts/check-dockerfile-restore-graph.sh` (with a `.ps1` twin) gates it in
  **both** `pr-verify` and `ci` — `ci` builds no images, and pr-verify's image job (CI-01 / #403)
  fires only on Dockerfile/.dockerignore/workflow edits, never on the project-graph changes that
  actually stale a COPY list. A new project must join every Dockerfile whose restore graph reaches it.
  **The Blazor exception (#414):** the frontend image's `dotnet build` runs **without**
  `--no-restore`. `blazor.web.js` ships in `Microsoft.AspNetCore.App.Internal.Assets`, which the SDK
  references only once it sees `.razor` files — never during the `.csproj`-only restore — so
  `--no-restore` there silently emits a static-web-assets manifest with no `_framework/*` and every
  asset 404s at runtime. Never add the flag back to that `dotnet build`; `publish` keeps it.
- **CI runs what the diff can actually break — nothing more (`scripts/ci/classify-changes.sh`).**
  Every PR gets three **independent** verdicts: `code` (restore + Release build + unit + integration
  tests), `guards` (the cheap bash gates CI *executes*: SSR purity, Dockerfile restore graph,
  migration safety, loop provenance) and `images`. Keep them independent — collapsing "CI executes
  this script" into "run the whole .NET gate" is what made a `.claude/` + docs + loop-scripts PR pay
  for a full Release build and both suites. The failure mode that matters is the other direction:
  putting a **build input** (`.editorconfig`, `Directory.*.props`, `global.json`, a fixture) into the
  inert list buys a silent false green, so `scripts/tests/classify-changes.tests.sh` pins both
  directions and runs **unconditionally** in the `changes` job, before any verdict is trusted.
  `ci.yml` (main) deliberately does **not** self-skip its .NET steps — CD is triggered by CI
  concluding success, so "CI was green" must keep meaning the build and both suites really ran; it
  filters cheap content with `paths-ignore` instead (no CI ⇒ no CD).
- **`GET / -> 200` never proves the Blazor frontend works.** A `[StreamRendering]` page returns 200
  with its spinner frame before it fetches anything. The signature of a healthy image is the
  **fingerprint**: `@Assets[]` renders `_framework/blazor.web.<hash>.js` only when `MapStaticAssets`
  resolved its manifest. `scripts/smoke.{sh,ps1}` leg 2 asserts exactly that, and CD smokes the
  0%-traffic candidate before any traffic shift.
- **Git is rebase-based, and branches are never deleted.** Integrate a feature branch off `main`
  with `git rebase main` — never `git merge main`; no merge commits in feature branches (remote
  `main` is squash-only, so history stays linear). After a PR's squash commit lands on `main`,
  **keep both the local and the remote branch** — merge with plain `gh pr merge --squash` (never
  `--delete-branch`), and never run `git branch -d/-D` or `git push origin --delete`.

## Code style (C#) — repo-authoritative

- **Sealed** all types unless there is explicit inheritance.
- **Explicit constructors** in classes — no primary constructors for a `class` (records are fine).
- `var` **only for anonymous types**; explicit types everywhere else.
- **LINQ methods**, never query syntax. **Pattern matching** — except inside a query expression.
- **File-scoped namespaces**, **Allman braces**, no `#region`.
- **Documentation uses `/// <summary>` XML doc comments** — never plain `//` comments to
  document a type or member. Omit entirely when the name already explains the intent; reserve
  plain `//` for the non-obvious *why* inline in logic.
- **Comments are written in simple English, level B1-B2 (#675).** Short sentences, common
  words, one idea per sentence. No chains of em-dashes, no stacked clauses, no vocabulary a
  non-native reader has to look up. A comment nobody can read on the first pass is a defect,
  even when every word in it is true.
- **A comment explains the *why*, never the *what*.** If the name and the code already say it,
  write nothing — `MarkAsProcessed` does not need "Marks the version as processed". Keep the
  reason and the pointer (`spec 0001`, `ADR-0047`, `#624`); leave the full story to the ADR or
  the spec, and never repeat the same explanation in a doc comment, an inline comment and a
  document at once.
- Code & identifiers in **English**.
- **Domain class member order — mirror TheKittySaver exactly** (golden refs:
  `…AdoptionSystem.Domain/Aggregates/CatAggregate/Entities/Cat.cs`, `…/Vaccination.cs`). Top to
  bottom: (1) `public const`s, (2) `private readonly` backing fields (e.g. child collections),
  (3) public properties, (4) public/internal behavior methods, (5) public/internal `static`
  factory method(s) (`Create`), (6) private constructors (domain ctor, then the parameterless EF
  ctor), (7) private helper methods. The `Create` factory sits **after** the behavior methods and
  **immediately before** the constructors — not at the top.

## Anatomy of a feature slice

### Patcher slice (stable — the reference shape; bugfixes & deliberate refactors)

`Application/Features/<Area>/`: `<Action>Command.cs` (sealed record `: ICommand<Result<T>>`) +
`<Action>CommandHandler.cs` (internal sealed, explicit ctor DI) + validator (commands only) +
response record. Wired in `ApplicationDependencyInjection`; CLI injects the closed interface and
maps failures via `ErrorMapper.MapErrorToExitCode`. Canonical examples: `Features/Patching/`,
`Features/PreflightChecking/`.

### TMS slice (the shape going forward — VSA in the API project)

```
TranslationSystem.API/Features/<Area>/<Action>.cs

internal sealed class <Action> : IEndpoint
{
    internal sealed record Command(…) : ICommand<Result<TResponse>>;          // or Query : IQuery<…>

    internal sealed class Handler : ICommandHandler<Command, Result<TResponse>>
    {
        // explicit ctor DI — command: repositories + IUnitOfWork + IValidator<Command>;
        // query: IApplicationReadDbContext (read models only)
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder) { … } // injects the closed
}                                                                             // handler interface
```

Wire the rest — **all three steps, every time**: (1) explicit DI registration of the closed
handler interface, (2) request/response DTOs in `TranslationSystem.Contracts`, (3) tests —
domain/handler unit tests + endpoint integration test against real PostgreSQL.
**Mirror the nearest existing sibling slice** (here or in TheKittySaver) rather than inventing
structure.

## Testing philosophy — repo-authoritative

- **Black box over the public seam — never the implementation.** Assert observable behavior:
  inputs in → `Result`/persisted state out. NSubstitute stubs **genuine boundaries**
  (`IDatFileHandler`, `IForumPageFetcher`), never internals you own.
- **`.Received()` policy:** only for side effects invisible in the return value (resource cleanup,
  "destructive op was NOT called on validation failure"). If the return value already proves it,
  `.Received()` is forbidden — a behavior-preserving refactor must never break a test.
- **Unit tests are pure:** no filesystem, no network, no DB, no order dependence. Real-resource
  verification belongs to integration projects.
- **The whole suite green locally before every push — unconditional.** `dotnet test`, no filter:
  everything runnable on this OS (the Windows-only `.Tests.Infrastructure` / `.Tests.E2E` suites
  auto-skip elsewhere). Never narrow it to the touched area, and never run integration only "when
  the slice ships an endpoint" — deciding from the diff which suites a change can reach is the
  mistake, not the shortcut. `pr-verify` runs unit **and** integration on every PR here, so a leg
  you skip locally does not disappear: it fails on a runner, at GitHub's expense, minutes after a
  local run would have shown it for free. Precedent from the sibling repo: TheKittySaver #567,
  where a CSS-only frontend fix broke four full-page snapshot tests nobody thought it could reach.
  A suite that genuinely cannot run here is unproven — say which, and do not open the PR.
- **Edge cases are first-class.** Happy path is the floor. `[Theory]` + `[InlineData]` for the
  unhappy-path/boundary matrix (empty, max, malformed, already-in-state).
- **AAA always; assertions inline in the test method.** DRY the Arrange (builders), never the
  Assert. One reason to fail per test.
- **Tooling: xUnit + Shouldly + NSubstitute only.** Naming: `MethodName_Scenario_ExpectedResult`.
  (`Architecture.Tests.Unit` additionally uses **NetArchTest.Rules** — architecture rules only;
  the two snapshot suites use **Verify.Xunit** — see the next bullet.)
- **Snapshots pin shape; they never replace an assert (#571).** Three tools, three jobs: **golden
  fixtures** own the `||` file contract on both sides (a snapshot adds nothing there and must not
  replace them), **plain asserts** own behavior across many inputs, and a **Verify snapshot** owns
  "did anything about this large payload change" — TMS API response bodies (JSON incl. HATEOAS links
  and ProblemDetails) and Blazor SSR rendered markup, where hand-written asserts only ever cover a
  corner. The behavioral suites stay; deleting an assert because "the snapshot covers it" is the
  wrong move. `*.verified.*` files are committed and ARE the pinned contract, `*.received.*` is
  git-ignored scratch, and **re-accepting a verified file is a deliberate, reviewed act** — read the
  diff, then land it in the same PR as the change that caused it. One shared scrubber set
  (`tests/Shared/VerifyModuleInitializer.cs`, linked into every snapshot suite) keeps runs
  deterministic; a snapshot that churns is worse than no snapshot. Details: `tests/CLAUDE.md`.
- **The structural house rules are a TEST, not review memory.** `tests/LotroKoniecDev.Architecture.Tests.Unit`
  mechanically enforces the patcher dependency rule, no-mediator (ADR-0001), patcher/TMS bounded-context
  isolation, the Frontend's contracts-only reach, the persistence direction, the CQRS read/write split and
  the sealed/`internal`-handler/commands-only-validator conventions — over assembly IL, in the normal unit
  gate, on every OS. **A new production project must join `ProductionAssemblies.All`** or it escapes every
  rule (a self-test fails until it does). Changing a rule is an architecture decision: fix the code, or
  write the ADR first — never weaken the test to green. Details: `tests/CLAUDE.md`.
- Platform honesty: tests must pass on macOS AND Windows — `Path.Combine`, never hardcoded `C:\`.
- **TMS test projects mirror KittySaver naming.** Unit (pure):
  `TranslationSystem.Domain.Tests.Unit`, `TranslationSystem.API.Tests.Unit`,
  `AuthSystem.API.Tests.Unit`, `SharedKernel.Tests.Unit`, `Logging.Tests.Unit`,
  `Frontend.Tests.Unit`. Integration (real
  PostgreSQL — never in a Unit project): `TranslationSystem.API.Tests.Integration`,
  `AuthSystem.API.Tests.Integration`. Browser/E2E (Testcontainers + Playwright — ADR-0009;
  Docker-required, off the PR gate by name): `TranslationSystem.E2E.Tests`,
  `Frontend.E2E.Tests`. All under `tests/LotroKoniecDev.<name>`. Patcher test projects
  (`Tests.Unit`, `Tests.Infrastructure`, `Tests.E2E`) stay exactly as they are.

## Workflow (the loop that compounds)

1. **Ticket before code.** Work flows from GitHub issues (`M{milestone}-{nn}: Title`, one
   `priority-*` + one `type-*` + any `area-*` — full taxonomy in **`docs/labels.md`**, kept in
   sync with TheKittySaver). Run **`/ticket <n>`**.
2. **Spec before code.** Anything non-trivial gets `docs/specs/NNNN-*.md` (via `/ticket` or
   `/spec`). Open questions are **extracted for the user, never invented**. Implementation starts
   only at **Status: Agreed**.
3. **Decision before code.** Non-trivial modeling/architecture choice → **`/adr`** first.
4. **Slice, mirror, test, review.** Branch via `gh issue develop <n> --checkout`. Implement by
   mirroring the nearest sibling slice, add tests, then run the **`code-reviewer`** agent on the
   diff (**`/security-review`** for anything touching native interop, file protection, or auth).
   **Green build + zero warnings + clean review = "done" — not before.**
5. **PR closes the ticket.** Title mirrors the ticket; body contains `Closes #<n>`.
   Push and open the PR without asking — the ticket is the authorization, and the PR is the last
   step, opened only when every gate is green (`/ticket` step 8); **merging is the one step that
   always needs a separate explicit ask.** **No merge with open CodeQL alerts:** green checks are not enough — the
   CodeQL check succeeds even when it uploads findings. Before any merge (interactive or loop),
   list `gh api "repos/{owner}/{repo}/code-scanning/alerts?ref=refs/pull/<n>/merge&state=open"`
   and fix every alert (dismiss only with a stated reason); the loop's merge gate enforces this
   mechanically.
6. **Feed the flywheel.** Reusable correction → persist it: global rule → **this file**; real
   decision → new ADR; empirical DAT/update finding → `docs/knowledge-base/` (dated). The same
   mistake made twice means a rule is missing.
   A lesson too narrow for any of those goes to `.claude/agent-memory/<agent>/`, which is
   **gitignored** — this repo is public, and one maintainer's notes about their own runs are not
   something a stranger should pull. So treat agent-memory as a scratchpad: if a lesson is worth
   keeping, it is worth promoting to a shared, checkable place from the list above.

### Manual QA — external testers execute what you write, literally

Manual QA runs on deployed **staging**, browser-only, by external testers with no repo access, no
Docker, no CLI — and no way to tell whether a ticket is still true. They pair with a general-purpose
LLM that has none of this repo's context, so a wrong scenario line does not get questioned: it gets
executed and reported as a bug. **Write QA tickets with `/qa-ticket`; re-baseline an existing one
with `/qa-ticket #<n>` before handing it over.**

- **An unverified scenario is a defect.** Every line must be backed by something you actually read
  in `src/` (the `.AllowAnonymous()`, the validator rule, the razor condition, the literal message
  string) or by a live query against staging. Of the first QA batch's 8 bug reports only 2 were
  valid findings; the misses trace to ticket lines that were stale (#603), unexecutable on staging
  (#547), contradicted by the code (#546), or invited browser-side fault injection (#602, #604).
  Tickets are written from `docs/specs/`, the product then moves, and nothing re-checks them — #271
  claimed the `polish.txt` download was auth-gated for a month after #309 made it public.
- **Classify every scenario:** plain (browser-only), _(owner-assisted — SKIP unless the owner runs
  it)_, or **blocked: what is missing**. Never "optional" — optional invites improvisation, and
  improvisation is what produced #602/#604. DevTools "Offline" is not API downtime: it cuts
  browser→frontend, and nothing server-rendered can render at all.
- **Every QA ticket carries a pointer to the wiki, never a copy of its rules (wiki verdict,
  2026-08-26).** The ticket opens with one line telling the tester to paste
  `Workflow-testera.md` into their assistant first; the exact wording lives in
  `.claude/commands/qa-ticket.md` §3, and `Workflow-testera.md` §13 owns it. Tickets used to
  carry a ~25-line "Read this before testing" frame, which was a copy of wiki §10 — so every rule
  change meant editing a dozen tickets, and they drifted from the page that outranks them. Rules
  live in exactly one place. The ticket says **what** to check; the wiki says **how** to test.
  Old tickets keep their frame until a `/qa-ticket #<n>` re-baseline strips it — a closed QA
  ticket is never re-run, so editing it buys nothing.
- **Every test step carries a stable id (owner rule, 2026-08-27, #742).** Each checkbox under
  `## Test scenarios` opens with `TC01`, `TC02`, … The id belongs to the **step, not its position**:
  a deleted step leaves a gap, a new one takes the next unused number, and a retired number is never
  reused — ids travel into the run report, bug titles and other tickets (#736 is titled after one),
  so renumbering invalidates those citations silently. Ids are unique per ticket and never restart
  inside the next scenario; `### S01 — name` grouping is optional and never enters the id, so the
  full id is always `QA-FE-{nn}-TC{kk}`. The wiki's `Workflow-testera.md` §13 owns this rule.
- **Hand over at most 3 tickets at a time**, and give every filed bug a verdict the same day
  (`valid` / `by design + why` / `needs retest`). A 15-ticket batch rots; without the feedback loop a
  tester keeps applying a wrong mental model and each wrong report is paid for twice.
- **A QA ticket closes when the run is reported, not when the bugs are fixed (owner rule, 2026-08-20).**
  A QA ticket records a *test execution*, so its done is: every scenario has a status, every non-pass
  has a same-day verdict, and every `valid` finding has its **own** ticket. Then it closes — with a
  run report — one comment per execution (wiki §5, #767). The retest obligation moves **onto the bug ticket**
  (an acceptance criterion naming the scenario, e.g. "re-run QA-FE-01 / `S01_PUBLIC` TC01"), because
  that is where someone will actually look. Never reopen a closed QA ticket: the product moved, so
  the next pass is a fresh, re-baselined run (`/qa-ticket #<n>`), not a resumed one. **The exception is
  `blocked:`** — a scenario that could not be executed produced no information, so that coverage does
  not exist yet and the QA ticket (or a follow-up run) stays open. `FAILED` = information obtained,
  work done; `blocked:` = work not done. Worked example: #262 closed at 6/7 with the failures carved
  out to #670 and #672.
- **A blocked run is labelled `qa-blocked`, by the tester (owner rule, 2026-08-21).** An open QA
  ticket does not say *why* it is open, so a run stalled on a missing precondition looks exactly like
  one nobody has started. The tester applies the label themselves (they hold write access) and names
  the missing precondition in a comment; `gh issue list --label qa-blocked` is then the owner's queue
  of things only the owner can clear. The owner removes the label when the precondition lands — that
  removal is the tester's signal to finish the run. The label tracks the *ticket*, so it comes off
  once nothing on it is blocked any more, not once the first blocked scenario clears.
- **Preconditions are the owner's job, not the tester's.** Non-default row states come from
  `scripts/qa/seed-staging.sql` (staging only; requires one approve in the UI afterwards to rebuild
  the artifact). Sample `exported.txt` files and the admin login are owner-provided — a scenario
  without its precondition ships as `blocked:`, never as a hopeful checkbox, and the ticket is handed
  over already carrying `qa-blocked` so the gap is on the owner's queue from day one.

### Loop mode — one ticket = one closed PR, in its own fresh headless process

Working the backlog autonomously has **two non-negotiables: one ticket = one closed PR (git
hygiene), and one ticket = one fresh context (cost + quality).** Different rules; both must hold.

**The loop is a SCRIPT, not a session — `scripts/claude/backlog-loop.sh` (the conductor).**
Deterministic bash picks the next ready ticket (`next-ticket.sh`: priority labels + the
`Depends on #X` gate + skip rules for qa/post-mvp/audit/Windows-only work) and runs it to completion in
a **fresh headless process** (`work-ticket.sh` → `claude -p "/work-ticket <n>"`). This repo is
public, so **only maintainer-written tickets may drive the loop**: `issue-trust.sh` refuses any
issue whose author *or any commenter* lacks write access, fails closed, and is enforced in front of
the session — naming a ticket explicitly cannot bypass it (ADR-0026). The per-ticket
session does the whole slice — spec weight, branch, implement, tests, `code-reviewer` gate,
commit → push → PR — then **dies**; the runner judges only its final `STATUS: DONE|BLOCKED` block,
waits for pr-verify, squash-merges (**never `--delete-branch`** — branches are kept), syncs main,
and moves on. No LLM context outlives a ticket, so per-ticket cost stays flat no matter how many
tickets run overnight. Earlier designs kept an orchestrator *session* alive across tickets (first
`/loop /ticket`, then a subagent-spawning `/backlog` orchestrator) — both accumulate N tickets'
returns in one context and re-read it every turn; that anti-pattern is retired. (Parallelism
across *independent* tickets would be a separate opt-in move — worktree per ticket; the loop is
deliberately serial.)

**Git hygiene — fully close each ticket before the next; never let two tickets' work share an
uncommitted working copy.** The runner enforces it mechanically: it refuses to start on a dirty
working copy, runs strictly serially, commits (never deletes, never stashes) anything a failed or blocked run
leaves behind on a dedicated `loop-salvage/<n>-<timestamp>` branch, and returns to a
freshly-pulled main between tickets. The worker (`/work-ticket`) never merges — the runner owns
the merge gate, and that gate also refuses any PR with **open CodeQL alerts** (fail closed; the
worker clears them first, see §5). BLOCKED tickets get the
`loop-blocked` label plus the open questions posted as an issue comment — triage is
`gh issue list --label loop-blocked` (raw per-ticket session logs stay in
`logs/claude-loop/<run>/` for debugging only). Business questions are **extracted for the user,
never invented** — that rule binds the worker and the conductor alike.

Entering loop mode (`/backlog`, or an explicit "work through the backlog") **is** the standing
authorization for commit → push → PR → merge — the merge, which `/ticket` never takes on its own
(§5 above), belongs to the conductor for the duration of the loop. A single wholesale lift (e.g. the AuthSystem module) is
**one ticket**: a large diff there is expected and fine — what's not fine is two tickets' worth of
files sitting uncommitted at once, or two tickets sharing one context. Full manual (overnight
runs, env knobs, triage, troubleshooting): **`docs/claude-loop.md`**.

## Roadmap (digest — details live as GitHub issues; `gh issue list`)

- **M2 — TMS backend — DONE** (core loop + update lifecycle — spec 0001): version-bound import
  of `exported.txt` + diff/invalidation, list/get/upsert/approve translations (approve clears
  invalidation + regenerates the artifact), translation-file distribution (pre-built artifact +
  ETag/304, `GET /translation-files/{lang}`), GameVersion endpoints, CLI auto-download (M2-20 —
  `Features/TranslationFileSyncing` + `UpdateChecking`). **The forum watcher (M2-18 / #85) is
  deliberately post-MVP** — owner decision (2026-06, ADR-0030): game-version registration stays
  manual, like the export→import pipeline. Don't work #85 without an explicit owner go-ahead.
- **M3 — Frontend (Blazor Static SSR) — DONE** (manual QA pass **QA-FE / #275 still open**):
  lifted OIDC infra; pages: dashboard, translation list, editor with `<--DO_NOT_TOUCH!-->`
  placeholder validation + approve flow, import/export, game-versions admin, "Moje konto"
  (data export + account-deletion UX — LEGAL-02) and terms of service (LEGAL-03).
- **M6 — Deployment — DONE:** CD over ssh + `docker compose` to a **Hetzner VPS** (Caddy ingress,
  Let's Encrypt TLS) + Neon Postgres, staging + prod two-stage promotion, secrets in a `chmod 600`
  `/opt/lotro/.env` per box, daily health ping (ADRs 0008–0029 for the pipeline; **ADR-0034** moved
  the hosting off Azure Container Apps on 2026-07-12, retiring the Terraform IaC, Key Vault, warm
  window and revision sweep with it — epic #486). Ops details: `docs/deployment/runbook.md`.
- **M4 — desktop player app (Avalonia — ADR-0033)** (not started; #41–#46): GUI over the patcher
  handlers + the same TMS auto-download the CLI ships in M2-20. MVP is Windows-only, but the
  framework choice keeps the Steam Deck/Proton path open (WPF was dropped — it's the one .NET UI
  framework that closes it; the Russian project's Elanor→Qt rewrite is the cautionary tale).
- **LEGAL — GDPR/compliance pack (epic #459, cut 2026-07-11):** two-phase account deletion,
  data export, ToS + privacy policy, cookie banner, self-hosted fonts.
- **M7 — Game-content catalog — NEXT UP (spec 0008, agreed 2026-07-06; epic #362, tickets
  #363–#375 cut, not started).** LOTRO Companion's
  lore XML imported as a catalog lens over the flat rows: catalog entries (quest/deed +
  registry-driven long tail incl. items) with role-tagged text slots joined **by
  `(FileId, GossipId)` keys, never text** (verified —
  `docs/knowledge-base/lotro-companion-data-model.md`); admin zip import (replace-per-kind, COPY
  idiom), catalog browser + per-entry/per-category Approved-based progress, atomic quest
  translation UX (entry page + editor context + entry bulk approve), translation→entry
  memberships. The lens never mutates translations and never triggers the artifact. Naming rule:
  **CatalogEntry, never "entity"** (DDD-Entity misconception — user decision 2026-07-06).
  **DoD:** import fixture → browse `/catalog` → translate a whole quest via editor context →
  entry bulk approve → artifact contains the rows (E2E).
- **Post-MVP backlog (deliberately cut from MVP):** glossary, `TranslationHistory`, bulk
  operations, keyboard shortcuts, AI review, Discord notifications, public API versioning,
  crowdsourced game-version reports, per-language roles, Companion data auto-fetch from GitHub.
  (The former "LOTRO Companion XML context import + quest browser" items were promoted to M7.)
  **Epic TP-00 (#377)** parks the post-M7 productivity/ecosystem pack with its evidence:
  TM-lite duplicate propagation (45% of corpus measured), Companion reference labels (named
  `${PLAYER}` placeholders + RU/DE/FR reference panel), per-patch worklist, launch sentinel
  (DAT-repair gap), lotro-data version watcher, glossary seed, quest arcs, `labels/pl` reverse
  export — promotion order on the epic; TP-01/TP-10 are `/spec`-first.

## Proactive command use

The `/ticket`, `/spec`, `/feature`, `/adr`, `/qa-ticket` workflows are model-invocable — reach for
them yourself when the request matches, without waiting for the user to type the slash:

- User references **a ticket number or pastes an issue** → run **`/ticket`**.
- User asks **which tickets are worth working / whether they make sense** ("czy te tickety mają
  sens", "co brać najpierw", "przejrzyj backlog zanim zaczniemy") → run **`/preflight`** over
  them — verdicts and lanes only, never start implementing from a list.
- User wants a **manual QA scenario written, refreshed or handed to a tester** → **`/qa-ticket`**.
  Same when triaging a tester's bug report: verify the claim against the code before answering, and
  re-baseline the QA ticket that produced it.
- User floats a **rough feature idea** with unclear business rules → **`/spec`** first.
- User describes a **concrete new use case** → mirror the nearest sibling slice (note:
  **`/feature` scaffolds the patcher-style Application slice** — for TMS slices mirror
  TheKittySaver + the de-mediatorization recipe until the skill is updated).
- User is **settling an architecture/modeling choice** → **`/adr`** first, then implement.
- Any **DAT binary format work** → hand off to the **`dat-format-expert`** agent.
- User says **"kontynuuj pracę w pętli" / "continue the loop" / "work through the backlog" /
  "jazda dalej"** (any keep-grinding-tickets phrasing) → invoke **`/backlog`**, which launches
  `scripts/claude/backlog-loop.sh` in the background — one fresh headless `claude -p` process per
  ticket. NEVER grind tickets inline in the current session and never spawn per-ticket subagents
  from it — both balloon one context, the exact anti-pattern Loop mode retires — and never route
  to `/loop`.

Don't narrate "I'll run the command" — just follow the workflow and report results. Never scaffold
off a vague one-liner: if a business rule is unclear, ask once, then proceed.
