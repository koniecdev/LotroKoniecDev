# ADR-0015: Derive cross-boundary dependency versions at runtime instead of hard-coding them (Playwright browser image ⇄ client)

**Status:** Accepted
**Date:** 2026-06-30
**Decision-makers:** Solo maintainer
**Related:** ADR-0009 (browser E2E via Testcontainers + Playwright — establishes the in-container
browser-server the client connects to over WebSocket); the trigger was PR #236 (Dependabot
`nuget-minor-patch` group), the fix + this ADR landed in PR #242; `tests/CLAUDE.md` (Frontend Browser
E2E section).

## Context

The Frontend browser-E2E stack (ADR-0009) runs Chromium in a **container** — image
`mcr.microsoft.com/playwright:v{X}-noble` — and the host `Microsoft.Playwright` **client** connects
to its in-container `run-server` over a WebSocket. The two must speak the **same wire protocol**: a
version mismatch makes `Chromium.ConnectAsync` fail the WS upgrade with **HTTP 428 Precondition
Required** ("Playwright version mismatch: server vA, client vB") during fixture init, taking the
whole suite down.

So the Playwright version is **one fact with two carriers, coupled across a process boundary**:

1. the `Microsoft.Playwright` **package version** (`Directory.Packages.props`), maintained by
   **Dependabot**, and
2. the browser-server **image tag** — until now a hard-coded `const string` in
   `PlaywrightStackFixture`.

Dependabot bumps (1) but cannot see (2): the version inside a C# string literal is opaque to it. PR
**#236** bumped the client `1.60.0 → 1.61.0`; the image const stayed `v1.60.0-noble`; the next
Frontend.E2E run broke with 428 — and because the suite is **off the PR gate by name** (ADR-0009),
the drift was invisible until someone ran it. A one-line const bump unbreaks it once, but the smell
guarantees a recurrence on the **next** Playwright bump. This is the general anti-pattern: *a value
that must track a Dependabot-managed dependency, hidden where the bot that maintains the dependency
can't reach it.*

A tempting "single source of truth" — read the driver version the client embeds
(`.playwright/package/package.json`) — is a **trap**: the `1.61.0` client embeds a driver labelled
`1.61.1-beta-…`, which maps to no published image. The image tags Microsoft publishes track the
**release/package version** (`v1.61.0-noble`), which is what the assembly reports.

## Decision

### 1. Derive the image tag from the client; never hard-code it

`PlaywrightBrowserImage.Tag` builds `mcr.microsoft.com/playwright:v{version}-noble` from the resolved
`Microsoft.Playwright` **AssemblyVersion** (`Assembly.GetName().Version`, e.g. `1.61.0.0 → 1.61.0`).
The fixture consumes `PlaywrightBrowserImage.Tag`; the const is gone. The package version is now the
**single source of truth** — Dependabot bumps it, the image follows on the next run, and the two can
never drift apart again.

### 2. Key off AssemblyVersion — not InformationalVersion, not the embedded driver

Two version carriers in the package are **traps**: the embedded driver build is a `-beta`
(`1.61.1-beta-…`) with no matching image, and — found the hard way here — `Microsoft.Playwright`'s
`AssemblyInformationalVersion` is a frozen **`1.0.0` placeholder** (`1.0.0+sha`), so a derivation
keyed off it silently yields the unpullable `v1.0.0-noble`. Only `AssemblyVersion` / `FileVersion`
carry the real release (`1.61.0`). Derivation reads **AssemblyVersion** (`ToString(3)`), strips any
SemVer build metadata defensively (`1.61.0+sha → 1.61.0`), and keeps `-noble` as a deliberate
constant — only the **version** is derived.

### 3. Lock the derivation with pure unit tests; no separate gate "drift guard"

`PlaywrightBrowserImageTests` (Docker-free — joins no collection) pins the derivation: base, a future
version, build-metadata stripping, the null/empty guards, and that `Tag` tracks the actually-loaded
client **cross-checked against an independent source (FileVersion)**. That independence is the point:
the first cut of this test compared `Tag` to the *same* InformationalVersion the (then-buggy) code
read, so it tautologically passed at `v1.0.0-noble` and only the end-to-end image pull caught it.
A gate-level "is the literal tag == the package version?" guard is **deliberately not added**:
derivation **eliminates** the drift class by construction, so there is nothing left to detect, and
putting it on the PR gate would drag the E2E project's Testcontainers/Playwright dependencies onto the
gate for zero benefit.

### 4. Generalize the principle

A value that must stay in lockstep with a **Dependabot-managed dependency across a process or artifact
boundary** is **derived from that dependency at build/runtime**, not duplicated as a literal the bot
can't update. (Out of scope here but flagged by the same lens: `axllent/mailpit:latest` is the
*opposite* smell — an unpinned tag — and is left as-is for now.)

## Consequences

### Positive

- **Zero-touch on Playwright bumps.** Dependabot's PR is now self-sufficient; the browser image
  tracks the client automatically. The recurrence is designed out, not patched.
- **Symmetric with existing code.** The `run-server` command already reads its `driverVersion`
  dynamically from the image; the tag now resolves dynamically from the client — both sides key off
  one version.
- **Failures get louder, not quieter.** The only residual risk — a release with no matching
  `-noble` image — surfaces immediately at fixture init as a clear image-pull error, strictly better
  than today's silent protocol drift caught off-gate.

### Negative / Accepted Trade-offs

- **Assumes `v{version}-noble` is published per release.** True for Microsoft's images; if it ever
  isn't, the pull fails loudly with an obvious cause (acceptable, and self-explaining).
- **The derivation tests live in the off-gate E2E project**, so they run in `e2e.yml` / local E2E,
  not on the PR gate. Accepted: derivation removes the drift the gate would have guarded, and the
  end-to-end `ConnectAsync` is the real proof.
- **Reflection at fixture startup** to read the assembly version — trivial, once per run.

## Alternatives Considered

### A. Derive the tag from the client assembly version (this ADR)

Chosen. Single source of truth, zero-touch, eliminates the failure class.

### B. Keep the literal tag + a gate guard asserting it equals the package version

Rejected as primary. Still a manual bump after every Dependabot PR (only louder/earlier), becomes a
tautology the moment you derive, and on-gate placement would couple the fast gate to E2E
dependencies. Its useful kernel — locking the logic — survives as the derivation unit tests.

### C. Move the image into a Dockerfile/compose tracked by Dependabot's `docker` ecosystem

Rejected. It would produce **two independent** Dependabot PRs (nuget + docker) that can merge at
different times → a transient-breakage window. It tracks the two versions in parallel instead of
**coupling** them.

### D. Pin the client back to match the image

Rejected. Fights Dependabot and reverts a wanted update; treats the symptom (client moved) instead of
the cause (a second, hidden source of truth).

## Implementation Notes

- `tests/LotroKoniecDev.Frontend.E2E.Tests/Infrastructure/PlaywrightBrowserImage.cs` — the
  derivation (`Tag`, pure `BuildTag`, `ResolveClientVersion`).
- `tests/LotroKoniecDev.Frontend.E2E.Tests/Infrastructure/PlaywrightBrowserImageTests.cs` — pure,
  Docker-free locks for the derivation logic.
- `tests/.../PlaywrightStackFixture.cs` — consumes `PlaywrightBrowserImage.Tag`; the hard-coded
  `PlaywrightImage` const is removed.
- The zero-warning solution build compiles the E2E project, so the helper + tests are covered by the
  build gate even though the suite itself runs off-gate (ADR-0009).

## References

- ADR-0009 — browser E2E via Testcontainers + Playwright (the in-container browser-server stack)
- PR #236 — the Dependabot `Microsoft.Playwright` 1.60.0 → 1.61.0 bump that exposed the drift
- PR #242 — this fix + ADR
- `tests/CLAUDE.md` — Frontend Browser E2E Tests section
- Playwright docs — versioned `mcr.microsoft.com/playwright` images; client/server protocol must match
