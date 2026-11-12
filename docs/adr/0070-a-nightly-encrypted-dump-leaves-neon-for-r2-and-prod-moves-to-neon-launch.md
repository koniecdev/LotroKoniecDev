# ADR-0070: A nightly encrypted dump leaves Neon for R2, and prod moves to Neon Launch

**Status:** Accepted
**Date:** 2026-11-12
**Decision-makers:** Solo maintainer (owner decision recorded in #688, 2026-11-12)
**Related:** [ADR-0014](0014-production-database-on-neon.md) (Neon is the database),
[ADR-0023](0023-forward-only-n-1-backward-compatible-migrations.md) (restore is the safety valve),
[ADR-0034](0034-hetzner-vps-instead-of-azure-container-apps.md) (the boxes; its alternative E),
[ADR-0050](0050-the-observability-backend-runs-on-the-staging-box.md) (where the alert runs),
[ADR-0065](0065-an-account-erasure-reaches-the-outbox-and-the-translation-system.md) (erasure);
tickets #688 (SEC-10), #336 (MIGR-01), #339 (MIGR-04), #50 (per-row history), #1332

## Context

Until now the only way back for the databases was Neon itself: point-in-time restore over a
6-hour window (the Free plan's limit) and one snapshot branch that each deploy makes before the
migrator runs. The runbook accepted that because the data could be re-created by hand, and named
the trigger to revisit it: the first real translators. That trigger fired on 2026-11-11, when an
outside translator started working on prod (#1332). His work cannot be typed in again.

Three kinds of loss are not covered by a 6-hour window:

- damage noticed late: a hijacked translator account that changes rows quietly for days;
- a bad migration older than the latest deploy (the snapshot covers only the latest one);
- losing Neon itself: the account, the project, or the vendor.

Facts that shape the decision:

- The Free plan gives each project 5 GB of network transfer a month, and a `pg_dump` from outside
  counts in full; compression does not help, it happens after the data left. Past 5 GB Neon stops
  the project's database until the next billing period. The translation database holds about 800,000
  rows, so one dump moves about 0.2 GB: a nightly dump alone is about 6 GB a month.
- The Free plan also stops the database after 100 CU-hours a month. Anyone who keeps it awake
  (traffic every few minutes) stops prod in about 16 days. On a paid plan the same load costs money
  instead.
- Neon's Launch plan has no minimum fee: $0.106 per CU-hour, $0.35 per GB-month of storage,
  $0.20 per GB-month of restore history, 500 GB of transfer included, and a history window of up
  to 7 days. Plans apply to a whole Neon organization.
- The database connection strings live in the prod box's `.env` and nowhere else. GitHub holds no
  database secret.
- The wiki says a restore of the whole database is no answer to a mistake, because it would also
  undo what happened since, an account deletion included (`Import-do-TMS`, "Ponowny import").

## Decision

### 1. Prod moves to Neon Launch; staging stays free

The production projects stay in the current Neon organization, and that organization moves to
the Launch plan. The staging projects move first to a new free organization: a transfer keeps the
connection string, so nothing goes down, and Neon does not move a project from a paid organization
to a free one. Settings on each production project: autoscaling with a maximum of 0.25 CU, scale
to zero on, a history window of 7 days (Launch starts at 1 day). The organization gets a spending
notification at $25 a month.

Why: the nightly dump needs more than 5 GB of transfer, and Launch gives 500 GB. It also turns two
ways to stop prod (too much transfer, a database kept awake) into a bounded cost: at 0.25 CU the
compute of one project costs at most about $19 a month even when it never sleeps. A normal month
is a few dollars. The 7-day history becomes the first step of every restore that it can reach.

### 2. A nightly encrypted `pg_dump`, run from the box's crontab

`scripts/hetzner/backup.sh` runs on the prod box as `deploy`, from its crontab, at 01:00 UTC. CD
copies it next to `deploy.sh` on every deploy, but it is not part of the rollback unit: it never
touches the running stack. For each database, auth first, it:

1. runs `pg_dump --format=custom` in a `postgres:18-alpine` container against the direct host
   (a pooled `-pooler` host is turned into the direct one);
2. has `pg_restore` read the whole dump, so a file that is not a complete archive never counts;
3. encrypts it with `openssl enc -aes-256-cbc -pbkdf2 -iter 600000` under `BACKUP_ENCRYPTION_KEY`,
   then decrypts it again and compares the bytes;
4. uploads it with `curl --aws-sigv4`, then reads the object's size and ETag (the MD5 of a
   single-part upload) back.

Only when every database landed does it write `lotro_backup_last_success_timestamp_seconds` into
a Prometheus text file. Auth goes first: an account erased between the two dumps is then still
present in the translation dump, never missing from the auth dump, and the erasure can be done
again (§6).

Why the box and not a scheduled GitHub workflow: the box already holds the connection strings, a
workflow would need a second copy of prod secrets in GitHub, and the `production` environment asks
for a reviewer, which a scheduled job cannot pass. Docker gives the box `pg_dump` without
installing anything.

### 3. A dedicated R2 bucket per environment, with its own token

The dumps go to Cloudflare R2, EU jurisdiction, in a bucket that holds nothing but dumps
(`lotro-backups-prod`), with an API token that can read and write that bucket only. A bucket lock
keeps every object for 29 days, so that token cannot delete a recent dump: whoever takes over the
box cannot wipe the backups along with the database. A third vendor means that losing the Neon
account or the Hetzner account does not take the backups with it.

### 4. Every object is ciphertext; the key lives on the box and in the password manager

AES-256 under one key per environment. Cloudflare holds only ciphertext. The key is on the box,
which already holds the database credentials, so a key there does not widen what a box compromise
exposes. A second copy sits in the owner's password manager: it is the one secret in the box `.env`
that cannot be minted again, because without it every dump is unreadable.

### 5. Retention: 30 days, as a lifecycle rule on the whole bucket

"Delete objects 30 days after upload" on the bucket: no script deletes anything, and with a bucket
that holds only dumps there is no prefix filter to get wrong. The privacy policy states the period.
An erased account's data therefore stays in encrypted dumps for up to 30 days after its erasure.

### 6. Restore copies rows back; it never replaces the prod database

Recovering from damage is: find the state before the damage in a scratch database (a Neon branch
at the minute before it while the 7-day history reaches; otherwise the last dump before it,
decrypted and restored into a scratch database), then copy back only the damaged translation rows
whose English text has not changed since, then rebuild the translation file. The scratch database
is dropped afterwards. `Translators` and every auth table stay as they are, so no erasure is undone.
A whole-database restore is for one case only, the loss of the Neon project. Then every erasure
done after the dump is done again, and every sign-in session is ended, before the site opens
(runbook → Database backups).

### 7. The alert reads the metric through the agent that is already there

The agent on each box exports `*.prom` files from `/opt/lotro/backups/metrics` through the
read-only root mount it already has. A Grafana rule pages when the last good run is older than
26 hours. It arms itself on the first good run, like the silent-box rule, so it can ship before
the bucket exists.

## Consequences

### Positive

- Damage noticed up to 7 days late is undone to the second; up to 30 days late, to the night
  before. Losing Neon costs at most one day of work.
- A failed night cannot pass as a good one: the metric is written last and only on full success,
  and the alert treats an old metric as red.
- Prod no longer stops at the end of a free allowance.
- No new secret in GitHub, and nothing installed on the box.

### Negative / accepted trade-offs

- **A bill.** A few dollars a month, about $19 per production project in a month where something
  keeps the database awake all the time. The spending notification only warns; it does not stop
  anything.
- **A night with the box down is a night without a backup.** The silent-box alert already fires.
- **When the staging box is down, nothing alerts**, this rule included. That gap is the runbook's
  "Not done yet" item, not new.
- **Retention is a dashboard setting** nothing in the code can see. The wiring step reads it back.
- **CBC has no integrity check of its own.** Changing an object needs the bucket's write token, the
  upload is read back, and a changed object fails at `pg_restore`.
- **One day of steps between dumps.** Rows written after the last dump and outside the history
  window are gone in a Neon loss.

## Alternatives considered

- **A nightly dump on the Free plan.** About 6 GB a month against a 5 GB limit: the dump itself would
  stop prod. Rejected.
- **A weekly dump on the Free plan.** Fits the limit, but losing Neon costs up to a week of work,
  and a database kept awake still stops prod. Rejected by the owner.
- **Dumping only rows changed since the last night, plus a weekly full dump.** Fits the Free plan,
  but a restore becomes a merge of many files that relies on every write moving `UpdatedAt`, and
  nobody has run it. Rejected.
- **Logical replication to another provider.** It counts as transfer, keeps the compute awake, and
  copies the damage along with everything else. Rejected.
- **Per-row history (#50).** It removes the late-noticed vandalism case properly and stays planned;
  it does not cover losing Neon or a bad migration. A complement, not a replacement.
- **Postgres on the box.** No allowance to run out of and no cold starts, but the box stops being
  disposable, point-in-time restore becomes our own WAL archive, and two prod databases share 4 GB
  with both app stacks. ADR-0034 (alternative E) rejected it while the backups would have shared the
  apps' failure domain; with dumps in R2 that half of the argument is gone. It is a separate
  decision, with real Neon numbers in hand, and is not needed for this one.
- **Hetzner Object Storage.** Hetzner is already a listed recipient, but the backups would sit in the
  same account as the boxes, for about €5 a month. Rejected for R2's independence.
- **`age` or a public-key scheme, so the box cannot decrypt.** The box already holds the live
  database credentials; it would add a tool and a second key to the restore. Rejected.
- **Streaming `pg_dump | openssl | curl` with no file on disk.** The read-back needs the size and MD5
  of what was sent, and a broken pipe would leave a partial object. The files live in
  `/opt/lotro/backups/work` (on disk; `/tmp` is RAM on these boxes) for seconds. Rejected.

## Implementation notes

- `scripts/hetzner/backup.sh`, its suite `scripts/tests/hetzner-backup.tests.sh` (pr-verify and
  ci), the classifier's guard inputs, and `deploy.yml`, which copies the script and creates the
  metrics folder on every box.
- `.docker/observability/alloy/config.alloy` (the `textfile` block) and
  `.docker/observability/grafana/provisioning/alerting/lotrokoniecdev-alerts.yaml`
  (`lotro-backup-missing`). Both are hand-deployed (ADR-0050).
- `.env.hetzner.example`: `BACKUP_ENCRYPTION_KEY`, `BACKUP_R2_ACCOUNT_ID`, `BACKUP_R2_BUCKET`,
  `BACKUP_R2_ACCESS_KEY_ID`, `BACKUP_R2_SECRET_ACCESS_KEY`.
- The privacy policy, sections 04 and 07, pinned by `PrivacyPolicyPageTests`.
- `docs/deployment/runbook.md` → "Database backups": the Neon plan, the wiring, the restore drill and
  the procedures. The Neon plan change and the crontab line are owner steps on the live systems.
