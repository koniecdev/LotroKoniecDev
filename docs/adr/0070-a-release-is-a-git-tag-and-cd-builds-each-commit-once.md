# ADR-0070: A release is a git tag, and CD builds each commit once

**Status:** Accepted
**Date:** 2026-11-12
**Decision-makers:** Solo maintainer (owner decisions recorded in #1344, 2026-11-12)
**Amends:** [ADR-0012](0012-continuous-deployment-pipeline.md) §1 and Alternative D (semver images
on `v*`), [ADR-0008](0008-cloud-agnostic-deployment-and-environment-strategy.md) §6 ("SHA +
semver"), [ADR-0024](0024-n1-backward-compat-ci-proof.md) Decision 1 and Alternative F (revisited,
unchanged)
**Related:** wiki page `Wersje-systemu` (what a release number means), epic #1346 (v1.0), #1344,
`.github/workflows/cd.yml`, `scripts/ci/resolve-prod-baseline.sh`, runbook "Cutting a release"

## Context

Until 2026-11-12 the project had no versions. No git tag ever existed. CD deploys every green commit
on `main` to staging, and prod takes them in batches after an approval.

On 2026-11-11 the first outside translator started working on prod. The owner decided to number
releases: 0.x now, and 1.0 for the release to players. The wiki page `Wersje-systemu` defines the
numbers and what 1.0 needs. It outranks this ADR.

`cd.yml` already listened to `v*` tags (ADR-0012 §1, ADR-0008 §6). A tag push built all four images
again, without the CI gate, and pushed them as `sha-<short>`, `X.Y.Z` and `X.Y`. A second build never
gives the same digest: the provenance attestation differs on every run, and the `apt-get` layer is
not pinned. So the first release tag would have moved `sha-<short>` of the commit prod runs to bytes
that no CI run and no staging deploy had seen.

It could be worse. The image is pushed before Trivy scans it, so a new CVE would leave the tag on an
image with no signature and no provenance. A CD deploy refuses such an image. The automatic rollback
and a hand-run `deploy.sh` check no provenance, so they would run it.

A manual `workflow_dispatch` had the same cause: it built the dispatched commit again.

Nothing reads version image tags. Deploy, rollback, the image scan and the runbook all use
`sha-<short>`. The only code that knew about them was a fallback in the prod baseline resolver.

## Decision

### 1. A release is a git tag plus a GitHub Release

- A release is an annotated tag `vX.Y.Z` on a commit that already runs on prod, plus a GitHub Release
  with notes for translators and players. The numbers follow the wiki page `Wersje-systemu`. They are
  not strict SemVer: X changes once, from 0 to 1, at the release to players.
- No workflow listens to the tag or the release. A release builds nothing and deploys nothing.
- The images of a release are the `sha-<short>` images of its commit. To deploy or roll back to a
  release, dispatch CD with that `sha-<short>`.
- Images get no version names (owner decision 1 in #1344). When something needs them (a retention
  policy, a pull by version), they are added by retagging the existing digest, never by a build.

### 2. CD builds each commit once

Before it builds, `build-and-push` looks up the commit's `sha-<short>`:

- The registry reports it as not found: build, push, scan, sign and attest, as before.
- It exists and carries this repo's build provenance: reuse it. Nothing is built or pushed, and the
  deploy takes that image.
- It exists without provenance: stop the job. Its first build failed after the push (Trivy, signing
  or attestation), or the check itself failed. CD never builds over an existing image.
- Any other registry answer: stop the job.

Builds of the same commit and image wait for each other instead of cancelling each other, because a
build cancelled after its push leaves an image with no provenance. The runbook says how to get past a
commit whose image failed after its push.

### 3. Only the server images

Rule 2 covers the four server images. The desktop app (M4, #45) will ship as an exe attached to the
GitHub Release, so its release does build something on purpose. Its own ticket decides how.

### 4. ADR-0024's "previous release" stays `HEAD^1`

ADR-0024 Decision 1 asked to revisit its definition once release tags arrive. Revisited: it does not
change. The N-1 proof needs the release that runs while a migration lands, which is the commit
deployed before it. A version tag is a bookmark on a commit, not a deploy unit: prod promotion is
batched and runs many times between two tags.

### 5. 0.x keeps breaking changes free

Owner decision 2 in #1344: in 0.x, breaking changes in the API and the code stay free, because every
client (website, CLI) ships from this repo and deploys with it. The database keeps the ADR-0023
rules, and the backup (#688) protects the translators' data.

## Consequences

### Positive

- The bytes behind `sha-<short>` never change after the first good build, so staging, prod and a
  rollback run the same image.
- Cutting a release cannot break a deploy, because it touches no image.
- A CI re-run or a dispatch of a commit that was already built costs no build.

### Negative / Accepted Trade-offs

- An image that failed after its push blocks its commit until someone deletes that package version
  in GHCR or merges a new commit. Before, a re-run built it again. This needs a Trivy, signing or
  attestation failure, and a Trivy failure on a new CVE usually needs a new commit anyway (#1328).
- GHCR shows no `:0.1.0`. To find the images of a release, look up the tag's commit first.
- A tag push runs the `cd.yml` of the tagged commit. A tag on a commit from before this change still
  builds again, so a release tag goes only on a commit that contains this change.

## Alternatives Considered

### A. Version names on images, added by retagging the digest

A `release.yml` on `v*` that adds `X.Y.Z` to the digest prod runs, with no build. Not chosen for now
(owner decision 1): nothing reads those names, and it is one more workflow with `packages: write`
that needs its own checks (provenance, never move an existing version, no moving `X.Y`). It stays
the way to add version names later.

### B. Keep the tag build, but do not push `sha-<short>` from a tag

Rejected: `X.Y.Z` would then name a fresh build that nobody ran, and a Trivy failure would leave it
unsigned.

### C. The box pins digests instead of tags

The strongest guard: a moved tag could never reach a box, even after a hand push to GHCR. Not chosen
now: the compose file, the box `.env`, the baseline resolver, the image scan, the hand procedure and
the box tests all work by tag. Rule 2 removes every way our own pipeline moves a tag.

### D. Build again over an existing image that has no provenance

Rejected: inside the job, "this image has no provenance" looks the same as "the provenance check
failed for a moment", and building over a good image is the bug this ADR removes.

## Implementation Notes

- `cd.yml`: no `push: tags` trigger and no `type=semver` tags. `build-and-push` starts with the step
  "Look up this commit's existing image"; every build, scan, sign and attest step runs only when it
  reports `exists=false`. Its concurrency group no longer cancels a running build.
- `scripts/ci/resolve-prod-baseline.sh` no longer tries `v<tag>` for a box-pinned tag, because the
  box can only pin `sha-<short>`.
- Runbook: "Cutting a release" and "An image that failed its checks".

## References

- #1344 (the bug and both owner decisions), epic #1346, wiki page `Wersje-systemu`
- ADR-0012 §1, ADR-0008 §6, ADR-0024 Decision 1, ADR-0023
- [docker buildx imagetools](https://docs.docker.com/reference/cli/docker/buildx/imagetools/inspect/)
