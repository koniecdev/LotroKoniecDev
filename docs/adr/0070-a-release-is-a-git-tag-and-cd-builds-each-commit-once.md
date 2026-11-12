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

**The tag comes last.** The build pushes the image by digest only, with no tag. Trivy, cosign and
the attestation work on that digest, and only then does the last step add `sha-<short>` (and
`latest` on the main tip). So a tag CD wrote always names a checked image. A build that stops half
way (a Trivy finding, a signing or attestation error, a cancel, a timeout) leaves only an untagged
digest, and the next run builds the commit again. The naming step never moves an existing
`sha-<short>` and reads it back afterwards. It copies the index with `--prefer-index=false`, so the
tag names the very digest that was signed.

**Before it builds**, `build-and-push` looks up the commit's `sha-<short>`:

- The registry reports it as not found: build, push, scan, sign, attest and name it.
- It exists and carries this repo's build provenance: reuse it. Nothing is built or pushed, and the
  deploy takes that image.
- It exists without provenance: stop the job. Either the check failed for a moment, or the tag was
  written before this ADR (older images have no provenance) or by hand. CD never builds over a tag.
- Any other registry answer, a refusal included: stop the job. A token refused for a moment may still
  be able to write, and a write could move a tag.

Both decisions live in `scripts/ci/commit-image.sh`, and `scripts/tests/commit-image.tests.sh` tests
them with stubs and pins the job's shape. Builds of the same commit and image wait for each other
instead of cancelling each other, so the second run reuses the first one's image.

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
- A build that fails half way leaves nothing to clean up: only an untagged digest stays behind.

### Negative / Accepted Trade-offs

- A commit whose `sha-<short>` was written before this ADR, or by hand, without provenance, cannot be
  built again until someone deletes that package version in GHCR or merges a new commit. This only
  matters for re-running CD on such a commit.
- `sha-<short>` (seven hex digits) is now the key for "already built". If two commits shared those
  seven digits, the second would reuse the first one's image. The odds are about one in a million per
  commit at today's size; before, the second commit would have moved the first one's tag instead.
- When a commit's images are reused, `latest` does not move. It is only a convenience for people.
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

### E. Keep the tagged push, and only reuse a tag that exists

The first version of this change. The tag then sits on the image while Trivy, cosign and the
attestation still run, so any failure after the push left a tag on an unchecked image, and the commit
stayed blocked until someone deleted it by hand. The rollback and a hand-run `deploy.sh` check no
provenance, so they would also run such an image. Naming the image last removes both.

## Implementation Notes

- `cd.yml`: no `push: tags` trigger and no `type=semver` tags. `build-and-push` starts with the step
  "Look up this commit's existing image" (`commit-image.sh find`); every later step runs only when it
  reports `exists=false`. The build pushes by digest (`push-by-digest=true`, no `tags`), and the last
  step, "Name the checked image" (`commit-image.sh name`), adds the tags. Its concurrency group no
  longer cancels a running build.
- `scripts/tests/commit-image.tests.sh` runs in pr-verify (guards), ci and actionlint. Besides the
  script, it checks that no workflow starts on a tag, a release or a new ref.
- `scripts/ci/resolve-prod-baseline.sh` no longer tries `v<tag>` for a box-pinned tag, because the
  box can only pin `sha-<short>`.
- Runbook: "Cutting a release" and "An image that failed its checks".

## References

- #1344 (the bug and both owner decisions), epic #1346, wiki page `Wersje-systemu`
- ADR-0012 §1, ADR-0008 §6, ADR-0024 Decision 1, ADR-0023
- [docker buildx imagetools create](https://docs.docker.com/reference/cli/docker/buildx/imagetools/create/)
  ("If only one source is specified and that source is a manifest list or image index, create
  performs a carbon copy")
- [Image and registry exporters](https://docs.docker.com/build/exporters/image-registry/)
  (`push-by-digest`)
