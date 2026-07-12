# HANDOFF — 2026-07-12 · branch `486-hetz-00-adr-0034-and-migration-plan` · epic #486

## Situation
Azure for Students sub DISABLED today (credits gone, renewal refused) → both prods 404
(lotro-translator.pl + uratujkota.pl). Nothing lost: Neon DBs, GHCR images, DNS, Brevo all fine.
Decision: migrate to ONE Hetzner VPS (CX32, amd64, Ubuntu 24.04). Owner wants **prod up TODAY**.

## DEVIATION from the original plan (owner decision, 2026-07-12)
Instead of ONE CX32: owner bought **TWO separate CX23 servers (x86, 40 GB, Nuremberg), Ubuntu
26.04 LTS** — location made the CX32 plan impossible. Split: **lotro-prod (167.233.159.221,
with backups) hosts BOTH prods** (lotro-translator.pl + uratujkota.pl); **lotro-staging
(91.98.74.228, no backups) hosts BOTH stagings**. Everything in the plan that says "the VPS"
now means "the pair"; compose/Caddy files land on both boxes, each with its env's vhosts only.
CX23 = 2 vCPU / 4 GB per box — tighter than CX32, watch memory with 4 app containers + Caddy.

## SERVER ACCESS (done 2026-07-12, this session)
- SSH aliases `lotro-prod` / `lotro-staging` in `~/.ssh/config`, key `~/.ssh/id_ed25519`
  (generated this session — the machine had NO prior keys).
- Root passwords rotated (Hetzner-mailed ones are burned — they were pasted into chat);
  new ones in git-ignored `.env.hetzner.local` at repo root → owner: move to password
  manager, delete the file. SSH password auth DISABLED on both
  (`/etc/ssh/sshd_config.d/00-hardening.conf`), key-only; passwords matter only for the
  Hetzner web console.
- DNS verified: all 6 hostnames resolve correctly (prod trio → prod IP, staging trio →
  staging IP; no `www` record — never existed, fine).

## DONE (verified)
- Incident analysis + memory updated (`finops-student-plan-cost-knobs`).
- Epic #486 + tickets #487–#492 created (as koniecdev — loop-trust OK).
- ADR-0034 (`docs/adr/0034-hetzner-vps-instead-of-azure-container-apps.md`) written.
- Playbook `docs/deployment/hetzner-migration-plan.md` written (phases, secrets matrix, gotchas).
- PR #493 open (docs-only: ADR + plan), branch pushed.

## PHASE-0 RESULT (2026-07-12 EOD): **BOTH ENVS LIVE ON HETZNER — smoke 10/10 each**
Prod (lotro-translator.pl) + staging up with LE certs; auth DBs reseeded (owner-approved
DELETE of ACA-era OIDC clients/admin → seeder recreated from fresh secrets); Brevo login fixed
(b025cc001@smtp-brevo.com — account e-mail does NOT work); deep auth /health green (db+smtp).
PR #494 open (stack files, Closes #488); evidence comment on #491. Admin password: scratchpad
`admin-pass.txt` + AUTH_ADMIN_PASSWORD in /opt/lotro/.env on the boxes — owner should log in,
then change it. Merge of #494 pending (pr-verify + CodeQL gate). Next: owner browser round-trip,
then #487/#489 hardening PRs, #490 CD-over-ssh, #492 Azure retirement, TKS twin epic.
OWNER DECISION (2026-07-12 evening): remaining TMS tickets (#487/#489/#490/#492 + backlog)
are DEFERRED TO THE NIGHT LOOP (`/backlog`); the immediate priority is uratujkota.pl bring-up —
full recipe written to `~/RiderProjects/TheKittySaver/HANDOFF.md` (fresh session there:
"kontynuuj"). NOTE for the loop: #488 is closed by PR #494 (merge it first if still open);
#491 needs only the owner browser round-trip + closing comment.

## PHASE-0 PROGRESS (2026-07-12, second session — earlier state)
- Secrets received (owner's `~/Desktop/cre.txt` — tell owner to DELETE it after migration).
- Branch `491-hetz-05-bring-up`, commit 75d654a: `compose.hetzner.yaml` (one file, both boxes,
  GHCR images, Neon, alias trick kept) + `.docker/hetzner/Caddyfile` (real ACME; TKS slots
  commented) + `.env.hetzner.example` + gitignore negation. NOT pushed yet (koniecdev token).
- **STAGING IS UP**: /opt/lotro on the box, GHCR login OK, LE certs issued, migrator ran
  ("already up to date" — the old Neon DBs SURVIVED with schema+data), health green.
  Smoke: 8/10 OK; legs 3–4 fail = OIDC clients in the surviving DB carry the OLD ApiClientSecret,
  and the old admin's password is unknown. Seeder is create-if-missing → fix = DELETE the two
  client rows + admin row, restart auth-api. **BLOCKED on owner consent** (classifier + AFK).
  Prepared: reseed.sql + conn-URI files (scratchpad); psql runs via dockerized postgres:18-alpine.
- **PROD NOT STARTED**: same files + env-prod ready in scratchpad; the up itself was
  classifier-blocked pending explicit owner go-ahead. Command = same as staging (scp 3 files,
  docker login via PAT file, pull, up -d).
- Brevo SMTP: key works as password but the LOGIN is unknown (account email FAILED 535 on
  smtp-relay.brevo.com:587) — owner must read the login (…@smtp-brevo.com) off the Brevo
  SMTP & API page; then fix Email__Username in /opt/lotro/.env on both boxes + restart auth-api.
- Conn strings use keyword format + `Timeout=60` (rides out Neon resume — the known race).
- Admin seed: koniecdev / koniecdev@gmail.com / generated password in scratchpad `admin-pass.txt`
  (also in both env files on the boxes as AUTH_ADMIN_PASSWORD).

## NEXT (in order)
1. ~~Merge PR #493~~ DONE (on main as d8c6aae). Servers bought, DNS set, SSH access ready
   (see sections above).
2. **Phase 0 bring-up per plan §Phase 0 — INTERACTIVE with owner** (= ticket #491, doing
   #487/#488/#489 minimally inline). Still needed FROM OWNER: Neon conn strings (fresh-minted,
   prod+staging × TMS+Auth), Brevo SMTP key (dashboard → SMTP & API), AUTH_ADMIN_* seeds;
   OpenIddict via scripts/gen-openiddict-keys.sh; GHCR read-only PAT (koniecdev) for the boxes.
   Session: bootstrap.sh (per box) → compose.hetzner.yaml + .docker/hetzner/Caddyfile → env
   files on the right box (prod env → lotro-prod, staging env → lotro-staging) → staging box
   first as rehearsal, then prod box → scripts/smoke.sh + manual OIDC round-trip.
3. Then harden into repo: #487, #488, #489 as proper PRs; #490 (CD over ssh); #492 (Azure retirement).
4. TheKittySaver: twin epic in its repo AFTER lotro prod is live.

## Gotchas
- Push/PR/issues need koniecdev token (memory `github-push-koniecdev-account`).
- DNS FIRST, ACME self-heals on retry; amd64 only; authority URLs must be public https origins.
- `GET / -> 200` proves nothing — smoke fingerprint leg is the check.
- compose.prod.yaml stays untouched (laptop parity stack).

Resume: `kontynuuj` (or `kontynuuj #491` for the bring-up directly).
