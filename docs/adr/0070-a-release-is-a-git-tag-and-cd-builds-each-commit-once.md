# ADR-0070: A release is a git tag, and CD builds each commit once

**Status:** Accepted (amended 2026-11-16 for desktop releases, #45, and 2026-11-22 for the release
script and the "Next release" issue, #1440: see the amendments below)
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

This section covers the server's releases. Desktop releases (`desktop-vX.Y.Z`) follow the
2026-11-16 amendment at the end of this ADR.

- A release is an annotated tag `vX.Y.Z` on a commit that already runs on prod, plus a GitHub Release
  with notes for translators and players. The numbers follow the wiki page `Wersje-systemu`. They are
  not strict SemVer: X changes once, from 0 to 1, at the release to players.
- No workflow listens to the tag or the release. A release builds nothing and deploys nothing.
- The images of a release are the `sha-<short>` images of its commit. To deploy or roll back to a
  release, dispatch CD from `main` with that `sha-<short>` as `image_tag`.
- Images get no version names (owner decision 1 in #1344). When something needs them (a retention
  policy, a pull by version), they are added by retagging the existing digest, never by a build.

### 2. CD builds each commit once

**The tag comes last.** The build pushes the image by digest only, with no tag. Trivy, cosign and
the attestation work on that digest, and only then does the last step add `sha-<short>` (and
`latest` on the main tip). So a tag CD wrote always names a checked image. A build that stops half
way (a Trivy finding, a signing or attestation error, a cancel, a timeout) leaves only an untagged
digest, and the next run builds the commit again. The naming step never moves an existing
`sha-<short>` and reads it back afterwards. `--prefer-index=false` makes the copy keep the digest, so
the tag names the very digest that was signed.

**Before it builds**, `build-and-push` looks up the commit's `sha-<short>`:

- The registry reports it as not found: build, push, scan, sign, attest and name it.
- It exists, and this repo's `cd.yml` built and attested it from this run's own ref: reuse it.
  Nothing is built or pushed, and the deploy takes that image. The check is
  `gh attestation verify --repo <repo> --signer-workflow <repo>/.github/workflows/cd.yml
  --source-ref "$GITHUB_REF"`.
- It exists, but that check fails: stop the job. Either the check failed for a moment, or the image
  was built from another ref (a run on a branch whose head is this commit), by another workflow or
  repo, or the tag was written by hand, or before this ADR by a build that came before signing began
  or failed after its push. CD never builds over a tag.
- Any other registry answer stops the job. That includes a refusal (a token refused for a moment may
  still be able to write, and a write could move a tag) and a missing child manifest of the image
  index, whose error line also ends in ": not found". Only the registry's own line for the tag,
  `ERROR: <ref>: not found`, means "missing".

The ref is the run's own, not a fixed `refs/heads/main`. A run started by CI, and a hand run from
`main`, both have `refs/heads/main`, so on `main` the two give the same answer. The run's own ref
also lets a second run on one branch head reuse that branch's images, and it is the ref the rollout
check reads in the same run once #1336 adds `--signer-workflow` and `--source-ref` there. So the
lookup reuses only what the deploy after it accepts.

Both decisions live in `scripts/ci/commit-image.sh`, and `scripts/tests/commit-image.tests.sh` tests
them with stubs and pins the job's shape. A second build of the same commit and image waits for the
running one instead of cancelling it, then reuses its image when both run from the same ref, and
stops when they do not. GitHub keeps one running and one waiting job per group, so a third run
cancels the waiting one. The group also keeps two runs of one commit from naming it at once.

### 3. Only the server images

Rule 2 covers the four server images. The desktop app (M4, #45) ships as an installer attached to
its own GitHub Release, so its release does build something on purpose. How it does that is in the
2026-11-16 amendment below: the trigger check in `scripts/tests/commit-image.tests.sh` stays as it
is, with no exception.

### 4. ADR-0024's "previous release" stays `HEAD^1`

ADR-0024 Decision 1 asked to revisit its definition once release tags arrive. Revisited: it does not
change. The N-1 proof needs the release that runs while a migration lands, which is the commit
deployed before it. A version tag is a bookmark on a commit, not a deploy unit: prod promotion is
batched and runs many times between two tags.

### 5. 0.x keeps breaking changes free

Owner decision 2 in #1344: in 0.x, breaking changes in the API and the code stay free, because every
client (website, CLI) ships from this repo and deploys with it. The database keeps the ADR-0023
rules, and the nightly off-platform backup (ADR-0071, live since 2026-11-15) protects the
translators' data.

## Consequences

### Positive

- The bytes behind `sha-<short>` never change after the first good build, so staging, prod and a
  rollback run the same image.
- Cutting a release cannot break a deploy, because it touches no image.
- A CI re-run, or a second run from the same ref, of a commit that was already built costs no build.
- A build that fails half way leaves nothing to clean up: only an untagged digest stays behind.

### Negative / Accepted Trade-offs

- A commit whose `sha-<short>` has no provenance (written by hand, or before this ADR by a build that
  came before signing began or failed after its push) cannot be built again until someone deletes
  that package version in GHCR or merges a new commit. This only matters for re-running CD on such a
  commit.
- A run stops when the commit's `sha-<short>` was built from another ref. A branch run whose head is
  a `main` commit, made before `main`'s CD built that commit, blocks it the same way: `main`'s run
  stops until someone deletes that package version or merges a new commit. Such a branch run has to
  be started by a writer, and before this ADR it would have moved the tag instead.
- A hand run with `image_tag` still builds the commit it runs on first, when that commit has no
  images yet. That is common after a commit that only changed docs, which starts no CI and so no CD.
  A failed build there stops the deploy, a rollback included (#1424).
- `sha-<short>` (seven hex digits) is now the key for "already built". If two commits shared those
  seven digits, the second would reuse the first one's image. The odds are about one in 400,000 per
  commit at today's size, and they grow slowly with the history; before, the second commit would have
  moved the first one's tag instead.
- When a commit's images are reused, `latest` does not move. It is only a convenience for people.
- A reused image is not scanned again. A CVE published after its build is caught by the daily
  `image-scan.yml` of what runs on the boxes, not by the CD run that reuses it. Scanning on reuse
  would block a rollback to an image that prod may already run.
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
the box tests all work by tag. Rule 2 removes the ways a `cd.yml` with this change moves a tag. A tag
can still move by:

- a hand push to GHCR, or any workflow on any branch with `packages: write`;
- a `v*` tag on a commit from before this ADR, which starts that commit's old tag trigger;
- a hand run of CD from a ref whose head is from before this ADR, which runs that ref's old `cd.yml`;
- a re-run of the build job of a CD run from before this ADR, which uses that run's old `cd.yml`
  (GitHub allows re-runs for 30 days);
- a registry that wrongly answers "not found" for a tag that exists, because CD trusts that answer;
- the runbook's own recovery for a stuck commit: delete its tagged version, then re-run, which gives
  the name new bytes. The runbook allows it only for an image no box runs.

The runbook forbids the third to fifth. The first is the reason this option stays open.

### D. Build again over an existing image that has no provenance

Rejected: inside the job, "this image has no provenance" looks the same as "the provenance check
failed for a moment", and building over a good image is the bug this ADR removes.

### E. Keep the tagged push, and only reuse a tag that exists

The first version of this change. The tag then sits on the image while Trivy, cosign and the
attestation still run, so any failure after the push left a tag on an unchecked image, and the commit
stayed blocked until someone deleted it by hand. The rollback and a hand-run `deploy.sh` check no
provenance, so they would also run such an image. Naming the image last removes both.

### F. Pin the lookup to the built commit (`--source-digest`)

Rejected. The attestation names the run's `github.sha` (the certificate of `sha-d9ec0f9` shows it).
For a run started by CI, `github.sha` is the tip of `main` when the run started, and CD builds the
commit CI tested, `workflow_run.head_sha`. The two can differ: CD run 43486613717 had `head_sha`
`d5d2077` and `github.sha` `a67f27e` (its gate stopped, so it built nothing). For such a build the
check would refuse a good image.

### G. Pin the lookup to a fixed `refs/heads/main`

Rejected. On every run from `main` it gives the same answer as `$GITHUB_REF`. It would stop every
second run on one branch head, the proof route of #1336 included, and it would differ from the ref
the rollout check reads.

### H. Only runs from `main` build or name `sha-<short>`

The only option that keeps an honest branch run from blocking a `main` commit. It is not a security
control: a branch can change its own `cd.yml`. It would break proving a build change from a branch,
and the branch deploys #1336 keeps for its proof route. Deferred: worth looking at again once #1336
lets only `main` use the environments.

### I. One "built by us" check shared by the lookup, the deploy and the rollback

The best end state: one definition of "ours". It needs the script #1336 adds to the deploy. Until
both are on `main`, the exact-command tests on both sides keep the two copies in step, and the change
that lands second can move the lookup onto the shared script.

## Implementation Notes

- `cd.yml`: no `push: tags` trigger and no `type=semver` tags. In `build-and-push`, no step before
  "Look up this commit's existing image" (`commit-image.sh find`, pinned to `cd.yml` and
  `$GITHUB_REF`) writes to the registry, and every later step runs only when it reports
  `exists=false`. The build pushes by digest (`push-by-digest=true`, no `tags`), and the last step,
  "Name the checked image" (`commit-image.sh name`), adds the tags. Its concurrency group no longer
  cancels a running build.
- `scripts/tests/commit-image.tests.sh` runs in pr-verify on every PR and in ci on main. Besides
  the script, it checks that no workflow starts on a tag, a release or a new ref.
- `scripts/ci/resolve-prod-baseline.sh` no longer tries `v<tag>` for a box-pinned tag, because the
  box can only pin `sha-<short>`.
- Runbook: "Cutting a release" and "An image that failed its checks".
- `commit-image.sh` reads digests with `--format '{{printf "%s" .Manifest.Digest}}'`: some buildx
  versions print the whole manifest view for a bare `{{.Manifest.Digest}}`.

## Amendment (2026-11-16): desktop releases

**Ticket:** #45 (M4-05). **Owner decisions:** the two `Owner decision` comments on #45, 2026-11-16.
**Spec:** [0016](../specs/0016-desktop-app-update-and-installer.md).

### What changes

**§1 now covers the server's releases only.** A server release stays an annotated `vX.Y.Z` tag on a
commit prod runs, with a release that builds nothing. The desktop app has releases of its own:

- A desktop release is the tag `desktop-vX.Y.Z` plus a GitHub Release with the installer and its
  SHA-256 file. Its numbers are separate from the server's (wiki `Wersje-systemu`).
- It comes from "Run workflow" on `main` (`.github/workflows/desktop-release.yml`, a
  `workflow_dispatch` with the version as input). The workflow checks the version, builds the exe and
  the Inno Setup installer, and installs, re-installs and uninstalls it on a Windows runner.
- **The tag comes last**, as an image name does in §2. Only after that smoke test does a job with
  `contents: write` create `refs/tags/desktop-vX.Y.Z` on the dispatched commit (a lightweight tag,
  made through the git refs API) and publish the release. A run that fails before that leaves no tag
  and no release, so a broken build never uses up a version number.
- A desktop release is never marked Latest. The server's release step passes `--latest`, so GitHub's
  "Latest" keeps pointing at a server release. The app finds its releases by the `desktop-v` prefix
  and never reads "Latest".

**§3: no workflow starts on a tag, still.** The rule and its test in
`scripts/tests/commit-image.tests.sh` stay without an exception. A release made with the workflow's
`GITHUB_TOKEN` starts no other workflow (GitHub does not start runs for events that token makes), and
no workflow listens to tags or releases anyway. The desktop workflow builds no image and has no
`packages: write`, so §2 is untouched.

### Alternatives considered for this amendment

- **A tag push starts the build (the owner's first wording, `push: tags: desktop-v*`).** Rejected by
  the owner's second decision. A failed build would leave a used-up tag, the trigger test would need
  an exception for one file and one pattern, and the run would use the workflow file of the tagged
  commit, not `main`'s.
- **A draft release the owner checks by hand.** Rejected by the owner: a hand step on every release
  gets forgotten, and the runner already installs and uninstalls the exact file.
- **A separate repository for desktop releases**, so `/releases/latest` would mean the desktop app.
  Rejected: it needs a write credential for another repo, and splits the code from its releases.
- **The translation server as the update source.** Rejected: every desktop release would need a
  server change, and an update would depend on the server being up.
- **`gh release create --target` creates the tag.** Rejected for the git refs API: `gh` does not fail
  when the tag already exists (it only refuses an existing published release) and then attaches the
  release to that tag's commit, so a race could publish an installer built from another commit. The
  refs API refuses an existing ref.
- **Require a green `ci.yml` run on the commit first.** Not adopted: `ci.yml` skips docs-only
  commits and cancels older runs, so the tip of `main` often has no run of its own and the check
  would block good releases. The runbook asks the owner to look at `main`'s CI before the dispatch.

## Amendment (2026-11-22): the release script and the "Next release" issue

**Ticket:** #1440. **Owner decisions:** the two "Owner decisions" sections in #1440's body
(2026-11-16 and 2026-11-22) and the `Owner decision` comment of 2026-11-22 on desktop tickets.

### What changes

**§1: a release is still a tag plus a release, now cut by `scripts/release.sh`.** The owner runs it
in a terminal, with no Claude session. A release is almost all waiting, and a session pays for every
status check. The steps of the runbook stay as the fallback.

- **Starting the script is the owner's approval of one prod deploy.** It picks the newest commit on
  `main` with a CI run, waits for that run, and deploys the commit if prod does not run it. A prod job
  waiting at the gate holds the `prod-mutation` group, so the release commit's own job cannot reach
  the gate behind it. The script therefore rejects every other run waiting at the gate (older and
  newer candidates and hand runs) and approves its own. The `production` environment allows
  self-review, so the owner's `gh` login can do that through the API.
- **The run name is the link from a commit to its CD run.** A CD run's own `head_sha` is main's tip
  when the run started, not the commit it tests and deploys (Alt F below has the same cause), and a
  deployment record names that same run sha. So `cd.yml` names each run
  `CD for <tested commit> (CI run <id>, attempt <n>)`, or `CD by hand: <tag or sha>`, and the script
  matches on that name. A hand run never counts as "prod runs the release commit", because its image
  tag can differ from its record.
- **A run whose prod job never started is run again in full** (`gh run rerun`, at most twice), never
  replaced by a hand dispatch with `image_tag`, which would skip the N-1 gate. A re-run keeps its
  event, so it reuses the images (§2) and proves the span again.
- **Nothing is published without the owner's yes** at the script's last prompt. Only a commit with
  this ADR is tagged, and nothing is tagged after a red check, a failed rollout or a rollback.
- **The notes come from one headless `claude -p` call** with no tools and no repo context, started
  from an empty folder, while the deploy runs. AI never blocks a release: when the call fails, the
  notes are the raw change list.

**A new signal: the "Next release" issue.** After every successful prod deploy, a job in `cd.yml`
keeps one open issue up to date with the changes that run on prod but are not released yet, and the
proposed number. It lives in `cd.yml` because events of the workflow token start no other workflow.
It takes the image tag the rollout resolved (a new output of `deploy.yml`), never `github.sha`, and
it has `continue-on-error`, so it never turns a good deploy red. It never says "release now": the
owner decides when.

**The proposed number.** A server closing issue with `type-feature` proposes the next minor,
otherwise one with `type-bug` the next patch, otherwise nothing. A closing issue whose title starts
with `M4-` is a desktop-app ticket: it is listed apart and never raises the server's number, because
desktop releases have their own numbers (the 2026-11-16 amendment, wiki `Wersje-systemu`). Closing
issues of other repositories (a dependabot PR's) never count. Nothing ever proposes 1.0.0 (epic
#1346).

**§3 is untouched.** The tag and the release the script makes start no workflow, and no workflow
listens to them.

### Alternatives considered for this amendment

- **A `/release` Claude command** (#1440's first plan). Replaced by the owner on 2026-11-22: the
  session pays for every status check, and it still needs the owner's yes.
- **A draft release the owner clicks.** Rejected by the owner: a hand step on every release gets
  forgotten.
- **A separate workflow on `deployment_status` or on `workflow_run` of CD** for the issue. Rejected:
  it cannot read `deploy-prod`'s outputs, and the deployment record names the run's commit, which is
  the wrong one after a docs-only tip or a hand run.
- **CD cancels older waiting candidates by itself.** Rejected: a waiting hand run (a rollback) must
  never be cancelled automatically, it needs `actions: write`, and the script would still have to
  clear the gate for its own run.
- **Find a run's commit in its gate job's log** (`deploy_sha=`). Rejected: it parses logs, and logs
  expire. The run name is set when the run is created, so it can be read before the approval.
- **Tell a docs-only commit by copying CI's `paths-ignore` into the script.** Rejected: a second copy
  of GitHub's glob rules drifts. The script reads the runs instead: every push to `main` gets a
  gitleaks run, and only a commit CI does not ignore gets a CI run.
- **A wall-clock grace before a commit counts as docs-only, and "the newest CD run created after
  CI started".** Rejected after a review: a delay in GitHub Actions would make the script release the
  commit below the tip, and a CD run of an earlier CI attempt can arrive late. The gitleaks run and
  the CI id and attempt in the run name are exact.
- **A release-please style bot.** Rejected: ticket titles here describe problems, so its changelog
  would be the raw titles, and it does not handle the prod approval.

## References

- #1344 (the bug and both owner decisions), epic #1346, wiki page `Wersje-systemu`
- #45 and spec 0016 (desktop releases, the 2026-11-16 amendment)
- #1440 (the release script and the "Next release" issue, the 2026-11-22 amendment)
- ADR-0012 §1, ADR-0008 §6, ADR-0024 Decision 1, ADR-0023, ADR-0071 (the backup)
- #1336 (which refs may deploy, and the rollout's provenance check), #1424 (a hand run with
  `image_tag` still builds first)
- [docker buildx imagetools create](https://docs.docker.com/reference/cli/docker/buildx/imagetools/create/)
  ("If only one source is specified and that source is a manifest list or image index, create
  performs a carbon copy")
- [Image and registry exporters](https://docs.docker.com/build/exporters/image-registry/)
  (`push-by-digest`)
