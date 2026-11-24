# ADR-0076: Third-Party Server Images Are Scanned Daily, and Every Exception Expires

**Status:** Accepted
**Date:** 2026-11-24
**Decision-makers:** Solo maintainer (ticket #1325, owner decision of 2026-11-24 on the issue)
**Related:** `.github/workflows/image-scan.yml`, `scripts/ci/list-third-party-images.sh`,
`scripts/tests/list-third-party-images.tests.sh`, `.github/trivy/`, `.github/dependabot.yml`,
`compose.hetzner.yaml`, `compose.prod.yaml`, `compose.observability.yaml`,
`scripts/hetzner/backup.sh`; ADR-0012 (H1), ADR-0050 §2, ADR-0060 §4, ADR-0067 §7, ADR-0070;
tickets #1325, #1326, #1001, #1521

## Context

Our own four images are scanned when CD builds them (`cd.yml`) and again every day
(`image-scan.yml`, #1326). The images we take from other projects were never scanned. Dependabot
proposed new versions of Caddy only (#1001). On 2026-11-11 Trivy found fixable HIGH and CRITICAL
findings in all seven images #1325 lists, and nobody had been told.

Nine third-party images run on the servers:

- `compose.hetzner.yaml`: Caddy and RabbitMQ, deployed by CD on every merge.
- `scripts/hetzner/backup.sh`: `postgres`, which runs `pg_dump` every night with the prod password.
- `compose.observability.yaml`: Alloy, Loki, Prometheus, Tempo, Grafana and an `alpine` preflight
  check, deployed by hand (ADR-0050 §2).

On 2026-11-24, even the newest releases left fixable findings in five of the nine: `x/net` in Caddy,
Tempo and Prometheus, an old built-in Tempo in Grafana, and in `postgres` one `libxml2` package plus
26 Go standard library findings in `gosu`. So a scan that fails on every finding is red from its
first day, and stays red with nothing to do.

Trivy cannot tie an ignore entry to one image. An entry has `id`, `paths`, `purls`, `expired_at` and
`statement`. For a finding in a binary, `paths` is the path inside the image, and the same
`x/net` finding sits in Caddy and in Tempo. `trivy-action` takes one ignore file per step. Trivy also
drops a key it does not know without a word, drops an expired entry without a word, and never
expires an entry that has no `expired_at`.

## Decision

### 1. The daily scan covers every third-party server image and fails on a fixable finding

`image-scan.yml` gets a second half, next to our own images: one leg per third-party image, with
the same Trivy gate as `cd.yml` (`HIGH,CRITICAL`, `--ignore-unfixed`, exit code 1). A red run sends
the same e-mail as today. The legs scan for vulnerabilities only (`scanners: vuln`): a secret inside
a vendor's image is nothing we can rotate. They give Trivy no registry credentials, because these
are public Docker Hub images and the token would go to Docker Hub (ADR-0067 §7).

### 2. It reads what each environment deployed

`scripts/ci/list-third-party-images.sh --matrix` reads `compose.hetzner.yaml` at each environment's
deployed commit, the same commit the own-image half uses, and `scripts/hetzner/backup.sh` at prod's
only, because every box gets the file but only prod's crontab runs it. It reads
`compose.observability.yaml` on the run's ref, because that stack leaves no deployment record. A deployed commit that is not a full SHA, or a file that cannot be read, becomes a leg that
fails with the reason, so the other images are still scanned. An empty commit is never read: `git
show :<path>` would read today's files and pass in silence.

### 3. One exception file per image, each entry with a class and an expiry

A finding that no vendor release fixes yet goes into `.github/trivy/<repository>.trivyignore.yaml`,
with `/` in the repository written as `-` (`grafana-grafana.trivyignore.yaml`). Only that image's leg
reads it, and our own images never read any of them. The file is JSON, which Trivy reads as YAML, so
`jq` can check it. Every entry has:

- `id`: one CVE, GHSA or Go (`GO-…`) id, the one Trivy reports;
- `purls`: exactly one package, with no qualifiers (`pkg:golang/golang.org/x/net`), and `paths`
  only to narrow it to one binary. A false positive names the package version too
  (`pkg:golang/stdlib@v1.24.6`): its proof holds for that version only, so a new pin with another
  version gets its finding back;
- `expired_at`: a real date at midnight UTC (`2026-12-24T00:00:00Z`);
- `statement`: one of two classes, then the reason.
  - `Waiting for a vendor fix: ` expires at most **30 days** after the day it was written.
  - `False positive: ` expires at most **180 days** after. A finding counts as one when its
    version label is wrong, or when `govulncheck` shows that the binary never calls the code.

`scripts/tests/list-third-party-images.tests.sh` checks every file on every PR, in `pr-verify` and
`ci`. It has no "already expired" rule on purpose: a date passing must never turn an unrelated PR
red, or stop CD through a red `ci` on `main`. The limit only gets easier as days pass. When an entry
expires, Trivy reports the finding again, so the daily scan turns red and someone renews or removes
it. A change to `.github/trivy/` alone ships nothing, so `ci.yml` ignores it (`paths-ignore`) and a
renewal starts no rollout.

### 4. Every server image is pinned by digest

RabbitMQ in `compose.hetzner.yaml` and `compose.prod.yaml`, and every image in
`compose.observability.yaml`, carry `tag@sha256:<digest>`, as Caddy and the backup image already
did. With a tag alone, the box keeps the bytes it pulled on deploy day, while the scan reads what
the tag points to on scan day. The self-test fails when a server image has no digest, or when the
list of nine changes, so a new image comes with a line in this ADR.

### 5. Dependabot proposes the observability images; RabbitMQ and postgres stay manual

The docker-compose Dependabot entry also proposes Grafana, Loki, Tempo, Alloy, Prometheus and
`alpine`, in one grouped pull request with the `on-hold` label (ADR-0060 §4), majors ignored. Caddy
keeps its own pull request. RabbitMQ and `postgres` stay manual, because each has a second pin that
Dependabot cannot move (`tests/Shared/RabbitMqImage.cs`, `scripts/hetzner/backup.sh`); the scan
says when they must move.

### 6. The observability stack stays deployed by hand

ADR-0050 §2 is unchanged. A merged observability pin reaches a box only with the next hand deploy
(runbook, "The observability stack").

## Consequences

### Positive

- A new fixable finding in any server image reaches the owner within a day.
- The reason each known finding stays is written in a file the scan reads, with a date by which
  someone looks again.
- One image's exception cannot hide the same finding in another image.

### Negative / Accepted Trade-offs

- **`gosu` turns the run red about once a month.** The `postgres` image ships `gosu` 1.20, built
  with go1.24.6, and `gosu` does not rebuild for Go fixes in code it never calls (its
  `SECURITY.md`). Almost every Go point release publishes new standard library findings, and each
  one needs a `govulncheck` run (runbook) and a new entry. Alternative E removes this cost.
- **One e-mail per run.** A red leg left standing hides the next finding. Fix it or add an
  exception within the week. After this change, prod's RabbitMQ 4.3.4 leg stays red until prod is
  promoted.
- **Two gaps.** The deployed commit comes from the deployment record, which can name another commit
  than the box runs (#1521). The observability half reads the pins on `main`, so a box that missed a
  hand deploy still runs older images while the scan is green.
- **Renewals cost a pull request**, at most every 30 days for each finding that waits for a vendor.

## Alternatives Considered

### A. Report only, never fail

Rejected by the owner. GitHub sends e-mail only for a failed run, so a green run that lists
findings tells nobody, which is the problem #1325 describes.

### B. Fail on every finding, with no exceptions

Rejected by the owner. Red from the first day on five images, with nothing to do, and it hides a
real red in our own images in the same run.

### C. Open or update an issue instead of failing

Rejected by the owner. It needs issue write access and de-duplication for the same signal the
failure e-mail already gives.

### D. Fail on CRITICAL only

Still red on the first day (the `gosu` CVE-2025-68121), and it breaks the `HIGH,CRITICAL` rule that
`cd.yml` uses.

### E. Our own backup image without `gosu`

An Alpine image with only `postgresql18-client`, built in CD, removes the 26 `gosu` entries and the
monthly renewals. Not now: it adds a fifth signed image to the build-once CD of ADR-0070 and changes
the restore drill, and the owner kept `postgres` a manual pin. Worth doing if the renewals become a
burden.

### F. One shared exception file with path rules

It works for an operating system package, whose Trivy target names the image. For a finding in a
binary, the scope would rest on the path inside each image being different, which nothing checks.
One file per image makes the scope structural.

### G. OpenVEX

A VEX statement can name an image, but it has no expiry and no status for "waiting for a vendor
fix".

### H. `--skip-files` for `gosu`

No expiry, and it would hide every future `gosu` finding too.

### I. YAML exception files, checked with `yq` inside the workflow

`yq` is not on the PC or in the guard check image, the check would have no test, and a broken entry
would show only in the daily run after the merge. JSON checked by `jq` in the tested script fixes all
three.

### J. The self-test also refuses an expired entry

A date passing would turn every PR red and stop CD through a red `ci` on `main`. The daily scan
already turns red when an entry expires.

### K. Scan the pins on `main` for every file

It contradicts #1326's reason for scanning what each environment deployed, and it would have missed
#1325's own case: `main` pinned Caddy 2.11.9 from 2026-11-10 while the boxes still ran 2.11.7.

### L. CD deploys the observability stack

It would remove the second gap. ADR-0050 §2 stands, by owner decision.

### M. Dependabot or Renovate for RabbitMQ and `postgres`

Each has a second pin Dependabot cannot move, and a parity test would not run on a pull request that
changes only compose files, which the change classifier calls inert.

### N. A scan on every pull request that moves a pin

Not needed now. For the observability group, `gh workflow run image-scan.yml --ref <branch>` scans
the proposal on demand, because that half reads the branch's own file. Caddy, RabbitMQ and
`postgres` are read at the deployed commits, so a new pin of theirs is scanned locally before the
merge (runbook, "The daily image scan"), and the daily run follows within a day of the deploy.

## References

- #1325 (this decision and the owner's answers), #1326 (the daily scan of our own images), #1001
  (Dependabot for Caddy), #1521 (the deployed-commit gap)
- Trivy v0.70.0 `pkg/result/ignore.go`, `filter.go`; `aquasecurity/trivy-action` v0.36.0
  `entrypoint.sh`
- `tianon/gosu` `SECURITY.md`; `golang.org/x/vuln/cmd/govulncheck`
- Runbook, "Observability & monitoring" → "The daily image scan"
