# ADR-0028: Make an incomplete Docker restore graph loud, and gate it before merge

**Status:** Accepted
**Date:** 2026-07-09
**Decision-makers:** Solo maintainer
**Related:** ADR-0008 (cloud-agnostic deployment; the four images this governs), ADR-0012 (CD —
every `main` commit builds and deploys the images), ADR-0023 / ADR-0024 (the sibling
"rule + pre-merge gate" pattern, and the precedent that a gate carries a `.sh` + `.ps1` twin),
`.github/workflows/pr-verify.yml`, `.github/workflows/ci.yml`, `.github/workflows/cd.yml`,
`scripts/check-dockerfile-restore-graph.sh`.

## Context

Every image Dockerfile copies the `.csproj` files first, runs `dotnet restore`, and only then
copies the sources. The restore then lands in a layer keyed on the project graph alone, so editing
C# never re-downloads NuGet packages. Three of the four Dockerfiles carried a hand-written list of
those `.csproj` paths.

`dotnet restore` does **not** fail when a `ProjectReference` points at a project file that is not
in the build context. It prints

```
Skipping project '/src/.../Projections.csproj' because it was not found.
```

and **exits 0**. The restore layer is then cached incomplete. Nothing downstream complains,
because `dotnet build` (run without `--no-restore`) quietly restores the gap again on every image
build — reaching for the network in a step that is meant to be offline.

The result is a defect that cannot be observed: it stays green in CI, green in CD, and green in
production. Three of the four Dockerfiles had been in that state, one of them since #136 added the
`Projections` project and did not add it to the TMS API image:

| Image | Silently skipped |
|---|---|
| `auth-api` | `Hateoas`, `Hateoas.Abstractions` |
| `frontend` | `Logging` |
| `tms-api` | `Projections` |

A hand-maintained list that no machine checks will drift. That is the real problem — the three
missing lines are only its symptom.

## Decision

Defend at two layers, because they fail at different times and neither subsumes the other.

**1. Make the toolchain loud (the source).** The three app Dockerfiles now run
`dotnet build --no-restore` and `dotnet publish --no-restore`, matching `Dockerfile.migrator`,
which already did. An incomplete restore layer now stops the image build with `NETSDK1004: Assets
file … not found` instead of silently re-restoring. This is the real `dotnet` graph enforcing
itself; it needs no model of the toolchain and cannot drift from it.

This is safe because `.dockerignore` excludes `**/bin` and `**/obj`, so the later `COPY . .`
cannot overwrite the assets the restore layer produced.

**2. Gate it before merge (the timing).** `pr-verify` and `ci` do not build images — only `cd`
does, after CI has already passed on `main`. So layer 1 alone reports the failure *after* the
merge, on `main`, which is exactly what a pre-merge gate exists to prevent.
`scripts/check-dockerfile-restore-graph.sh` (with its `.ps1` twin, per ADR-0024) therefore runs in
both workflows, before `setup-dotnet`: for every Dockerfile that lists `.csproj` files, it derives
the restore roots from that Dockerfile's own `dotnet restore` commands, walks the real
`ProjectReference` graph on disk, and fails unless the `COPY` list covers the transitive closure.

The `.csproj` graph is the single source of truth. The Dockerfile has to match it, never the other
way round. Dockerfiles that copy whole source trees (`Dockerfile.migrator.prod`,
`Dockerfile.tests`) have no list to go stale and are skipped.

`Dockerfile.migrator` also moves from whole-tree copies to the same `.csproj`-first layering, so a
C# edit stops re-downloading every NuGet package. That does create a list where none existed — a
deliberate trade, made affordable by the two layers above.

## Consequences

- A dropped `COPY ["…csproj", …]` now fails twice: at PR time by the guard, and at image-build
  time by `NETSDK1004`. Both were verified by deleting the `Projections` line and observing each.
- Adding a project to the solution is no longer complete until it joins every Dockerfile whose
  restore graph reaches it. The guard says which ones, by name.
- Image builds get marginally faster and genuinely offline after restore.
- Dockerfiles join `force_build` in `pr-verify`'s change gate: a Dockerfile-only PR now runs the
  full gate rather than skipping it, because the guard reads Dockerfiles. Docs-only PRs still skip
  (the skipped required check reports success — that behavior is untouched).
- Cost: the guard is ~350 lines across two shells that must return identical verdicts. Two
  PowerShell-specific traps are already documented in the twin (a one-element `0..-1` slice
  duplicates instead of popping; `-notcontains` is case-insensitive while the Linux image build and
  the `.sh`'s `comm` are not). Every future restore edge has to be taught in both. Layer 1 is what
  keeps this cost bounded: if the guard's static model ever misjudges, the real toolchain still
  fails the build.

## Alternatives Considered

**`COPY --parents src/**/*.csproj ./` — rejected.** One line per Dockerfile, no list, no guard.
It requires a `# syntax=docker/dockerfile:1.7-labs` frontend directive, which pulls an unpinned,
labs-channel BuildKit frontend from Docker Hub at build time. This repo pins every base image by
`@sha256`, scans with Trivy, signs with cosign, and verifies SLSA provenance before rollout
(ADR-0012). Trading a checked list for an unpinned upstream build-time dependency is a
supply-chain regression in precisely the pipeline whose point is pinned, attested inputs. Revisit
if and when `--parents` leaves the labs channel.

**`--no-restore` alone, no guard — rejected.** It is the better mechanism but it fires in `cd`,
after the merge. Main goes red instead of the PR.

**Build the images in `pr-verify`, then delete the guard — rejected for now.** The real toolchain
would be the gate, and ~350 lines would disappear. It costs a full four-image `docker build` on
every PR. On a student-plan budget (see the FinOps work in ADR-0027) that is the wrong trade
today. This is the alternative to revisit first if the guard ever becomes a maintenance burden.

**Do nothing — rejected.** The bug is invisible by construction. It survived from #136 to now.

## Implementation Notes

- The guard derives roots from the Dockerfile's own `dotnet restore` lines rather than a hardcoded
  list, so it cannot disagree with the file it checks.
- It joins `\`-continuation lines before scanning. That is a no-op on today's Dockerfiles, and is
  kept because a restore root wrapped onto a bare continuation line would otherwise drop out of
  the closure and the guard would pass vacuously.
- It reports two distinct failures: a `ProjectReference` whose file is missing on disk (a broken
  reference), and a project in the closure that the Dockerfile never copies (a stale list).
- Verified red on the pre-fix tree — exactly the three gaps in the table above — and green after.

## References

- `scripts/check-dockerfile-restore-graph.sh`, `scripts/check-dockerfile-restore-graph.ps1`
- `.github/workflows/pr-verify.yml`, `.github/workflows/ci.yml`
- CLAUDE.md → house rule "Docker restore layers are gated"
- ADR-0024 (twin-script precedent), ADR-0023 (rule + pre-merge gate precedent)
- #136 (added `TranslationSystem.Projections`; the TMS API image never learned about it)
