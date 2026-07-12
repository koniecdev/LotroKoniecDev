# Hetzner runbook — VPS hosting (ADR-0034)

> Operator manual for the Hetzner boxes that host prod + staging of BOTH projects
> (LotroKoniecDev and TheKittySaver). Decision: `docs/adr/0034-…`; executable migration
> playbook: `docs/deployment/hetzner-migration-plan.md`; epic #486.
>
> **Deviation from ADR-0034 §1 (owner decision, 2026-07-12):** instead of one CX32 the fleet is
> **two CX23 boxes** (location constraints at purchase) — `lotro-prod` hosts BOTH prods,
> `lotro-staging` hosts BOTH stagings. Everything the ADR/plan says about "the VPS" applies to
> each box of the pair.
>
> Scope today: **server provisioning + facts** (HETZ-01/#487) and the **secrets matrix**
> (HETZ-03/#489). The CD-over-ssh pipeline (HETZ-04/#490) extends this file when it lands. Until
> HETZ-06/#492 retires the Azure content, `docs/deployment/runbook.md` remains the reference for the
> OIDC/issuer gotchas — those are platform-independent and still binding.

## Server facts

| Fact | Value |
|---|---|
| Fleet | `lotro-prod` **167.233.159.221** (both prods) · `lotro-staging` **91.98.74.228** (both stagings) — ssh aliases of the same names in the owner's `~/.ssh/config` |
| Provider / model | Hetzner Cloud **CX23** × 2 (2 vCPU, 4 GB RAM, amd64) — tight for 4 app containers + Caddy per box; watch memory before adding services |
| Location | Nuremberg (nbg1) |
| OS | Ubuntu 26.04 LTS (resolute); `bootstrap.sh` is verified on 24.04 and 26.04 |
| Backups | Hetzner backups on **lotro-prod only** (~20% surcharge); staging is disposable |
| Users | `root` (key-only, Ubuntu default `prohibit-password`) · `deploy` (docker group, key-only, locked password — CD and day-2 ops run as this user) |
| Firewall | ufw: deny incoming except **22/80/443**, allow outgoing |
| Intrusion / patching | fail2ban (sshd jail, systemd backend) · unattended-upgrades |
| Container runtime | Docker Engine + compose plugin from Docker's official apt repo |
| Registry | `deploy` is `docker login`-ed to **ghcr.io** with a **read-only** (`read:packages`) PAT |
| DB | none on the box — Neon stays the DB for prod AND staging (ADR-0034 §2) |

Images are `linux/amd64` only (no multi-arch buildx) — the box is amd64 on purpose; never "fix"
a pull error by enabling emulation.

## Filesystem layout

| Path | Contents | Owner |
|---|---|---|
| `/opt/lotro` | LotroKoniecDev stack: `compose.hetzner.yaml`, `.docker/hetzner/Caddyfile`, `.env` (`chmod 600`, never committed — template: `.env.hetzner.example`) | `deploy` |
| `/opt/tks` | TheKittySaver stack (its own twin epic; joins the same Caddy via the parametrized vhosts landed in #495) | `deploy` |

The same layout exists on BOTH boxes; the single `/opt/lotro/.env` per box is what differentiates
them (`COMPOSE_PROJECT_NAME=lotro-prod` vs `lotro-staging`, domains, image tag) — see the header
of `compose.hetzner.yaml`.

## Secrets and env vars

**`compose.hetzner.yaml` is the authoritative list** of what the stack consumes — a variable that
does not appear there does nothing, whatever this table says. The live values sit in
**`/opt/lotro/.env`** on each box (`chmod 600`, owner `deploy`): **one file per box**, because one
box = one environment. (The migration plan drafted `.env.hetzner.prod` + `.env.hetzner.staging`
side by side — that shape belonged to the single-CX32 design; with the two-CX23 fleet each box
carries exactly one env file, named plainly `.env` so compose picks it up with no `--env-file`.)
The tracked template carrying every key with placeholder values is **`.env.hetzner.example`**.

**Nothing below was recovered from Azure — every value was re-minted, and it had to be**
(see §Key Vault recovery attempt).

### Secret material — source of truth and how to rotate

| Variable | Consumed by | Source of truth | (Re)mint / rotate |
|---|---|---|---|
| `ConnectionStrings__AuthDatabase` | migrator, auth-api | **Neon** — the env's project (prod: `lotro-translator-prod`; staging has its own, ADR-0018), DB `lotro_auth` | Neon console → Roles → *Reset password* → rebuild the string by hand. **Keyword format only** — Npgsql does not parse `postgres://` URIs. Must carry `Ssl Mode=Require` and `Timeout=60` (rides out Neon's ~31 s scale-to-zero resume). |
| `ConnectionStrings__TranslationDatabase` | migrator, tms-api | same Neon project, DB `lotro_translation` | same |
| `OpenIddict__SigningKey__RsaPrivateKeyXml` | auth-api | **regenerate** — no canonical copy exists anywhere | `scripts/gen-openiddict-keys.sh` prints all three OpenIddict values as `KEY=VALUE` lines; append to the box `.env`. Pure config (never stored in the DB), so rotating just invalidates issued tokens → everyone re-logs in. Free pre-launch. |
| `OpenIddict__EncryptionKey__Key` | auth-api | regenerate | same script, same blast radius. |
| `OpenIddict__ApiClientSecret` | auth-api (**seeds** the `lotrokoniecdev-api` client row) | regenerate | same script — **but the DB row wins on restart; rotation needs the reseed below.** Must stay equal to the `SMOKE_CLIENT_SECRET` GitHub secret of the same environment. |
| `Email__Username` | auth-api | **Brevo** dashboard → SMTP & API → SMTP keys | The SMTP **login**, shaped `<id>@smtp-brevo.com` — **not** the Brevo account e-mail, which fails the handshake with `535`. Read it off the Brevo dashboard; the value in use sits in the box `.env`. |
| `Email__Password` | auth-api | **Brevo** (owner pastes) | Generate a new SMTP key in Brevo. Shown **once** and never readable back — the copies in the box `.env` and in GitHub secrets are both write-only, so a lost key is re-generated, never recovered. |
| `AUTH_ADMIN_PASSWORD` (+ `AUTH_ADMIN_USERNAME`, `AUTH_ADMIN_EMAIL`) | auth-api → `AdminUser__*` seeder | **owner-chosen** | Seeded **only when missing**, so editing `.env` never rotates a live admin — see the reseed traps. `AUTH_ADMIN_USERNAME` must match `^[a-zA-Z0-9]+$` (ADR-0022) or auth-api fails at startup. |
| `SMOKE_CLIENT_SECRET` *(GitHub secret, per environment — **not** a box var)* | `scripts/smoke.sh`, CD (#490) | == `OpenIddict__ApiClientSecret` of that env | `gh secret set SMOKE_CLIENT_SECRET --env <staging\|production> --body "$VALUE"` — **never `--body -`**: gh takes `-` literally and the candidate smoke then 401s. |
| GHCR pull token *(not an env var — `docker login` state in `/home/deploy/.docker/config.json`)* | `docker compose pull` | **GitHub PAT**, scope `read:packages` **only** | Re-run `scripts/hetzner/bootstrap.sh` (its login leg prompts for user + PAT on a TTY). |
| Deploy ssh key | CD over ssh (#490) | generated for CD | Lands with HETZ-04 — the `deploy` user's key, never `root`'s. |

Not secrets, but they decide which environment a box *is* — full list with placeholders in
`.env.hetzner.example`: `COMPOSE_PROJECT_NAME` (`lotro-prod` \| `lotro-staging`), `IMAGE_NAMESPACE`,
`IMAGE_TAG`, `DOMAIN_APP` / `DOMAIN_AUTH` / `DOMAIN_TMS`, `ACME_EMAIL`, `TKS_DOMAIN_*` (the guest
TheKittySaver vhosts), `Email__Host` / `Email__Port` / `Email__Mode` / `Email__SenderEmail` /
`Email__Sender`, and `OTEL_EXPORTER_OTLP_ENDPOINT` (empty = exporter off).

Two pieces of credential-ish material live **outside** `.env` and outside the DB: the
`auth-keys` and `frontend-keys` **Data Protection keyring volumes**. They are not minted from
anywhere — losing a volume simply invalidates every auth cookie and OIDC correlation state
(everyone re-logs in), which is why they are named volumes and not bind mounts.

### Reseed traps — the auth seeder is create-if-missing

`SeedAuthDatabaseAsync` (`AuthSystem.API/Extensions/DatabaseSeederExtensions.cs`) creates only what
is absent: the admin user is skipped when its e-mail **or** username already exists, and each
OpenIddict client is skipped when its `client_id` already exists. The Neon DBs **outlived Azure**,
so this is the normal case — and it means **editing `.env` and restarting silently changes nothing**:

| You changed in `.env` | What silently keeps the OLD value | Symptom |
|---|---|---|
| `OpenIddict__ApiClientSecret` | the `lotrokoniecdev-api` client row | `client_credentials` with the new secret → **401**; smoke's token leg fails |
| `AUTH_ADMIN_PASSWORD` | the admin `Users` row | the old password still logs in; the new one never works |
| `DOMAIN_APP` | the `lotrokoniecdev-web` client's redirect + post-logout URIs (written **only at creation**) | login bounces with `invalid_redirect_uri` |

Fix = delete the rows and let the seeder rebuild them from the current `.env`. Schema is
**`authsystem`** and the Identity tables are **renamed** (`authsystem."Users"`, *not* `AspNetUsers`).
Delete in FK order:

```sql
DELETE FROM authsystem."OpenIddictTokens";
DELETE FROM authsystem."OpenIddictAuthorizations";
DELETE FROM authsystem."OpenIddictApplications";
-- only when rotating AUTH_ADMIN_* (UserRoles cascades with the user):
DELETE FROM authsystem."Users" WHERE "Email" = '<admin e-mail>';
```

```bash
docker compose -f compose.hetzner.yaml restart auth-api   # seeder runs at startup, recreates the rows
```

Every logged-in user is signed out by this (their tokens are gone) — free pre-launch, a real
outage after launch.

### Key Vault recovery attempt (recorded 2026-07-13 — do not retry)

The plan allowed one shortcut attempt at reading the old secrets out of Azure Key Vault before
re-minting. Result, from the owner's machine with `az` still logged in (subscription **Disabled**):

| Call | Outcome |
|---|---|
| `az keyvault list` | **works** — all four vaults still enumerate (management-plane read) |
| `az keyvault secret list --vault-name lotrotms-kv-{prod,staging}` | **works** — returns the 8 secret **names**: `admin-password`, `connection-string-auth`, `connection-string-translation`, `openiddict-api-client-secret`, `openiddict-encryption-key`, `openiddict-signing-key`, `smtp-password`, `smtp-username` |
| `az keyvault secret show --name <any>` | **Forbidden** — `The subscription associated with this vault has been disabled.` |

**Outcome: no secret value is recoverable from Key Vault.** The data plane serves metadata but
refuses every *value* read while the subscription is disabled — so the vault tells you only what
used to exist, never what it was. That is why every secret on the Hetzner boxes was re-minted from
the table above. `scripts/seed-keyvault.sh` (whose name→env mapping is the historical index of what
lived in the vaults) retires with the rest of the Azure surface in #492.

### Hygiene

- The box `.env` is the **only** live copy of the set → the owner keeps one encrypted off-box copy
  (password manager). Losing it costs a re-mint plus the reseed above — never data.
- `.gitignore` ignores `.env`, `.env.*` and `*.env`, whitelisting only the placeholder examples, so
  a filled env file cannot be committed by accident; the required **GitGuardian** check on `main` is
  the second net. Values travel to a box over ssh only — never through an issue, PR, or commit.

## (Re)provisioning a box

1. **Owner:** Hetzner console → create the box (Ubuntu LTS, backups on for prod) with the
   owner's ssh **public key** → note the IP.
2. **DNS first** (ACME needs it resolving before certs can issue): registrar panel → A records
   for every public hostname → the new IP, TTL 300. Hostname list = the env matrix in
   `docs/deployment/runbook.md` — keep hostnames identical to the ACA-era values. Prod trio →
   prod IP, staging trio → staging IP.
3. **Bootstrap** (idempotent — re-running changes nothing). Preferred form (`-t` gives the GHCR
   prompt a TTY, so the PAT is typed at a hidden prompt and never lands in shell history):

   ```bash
   scp scripts/hetzner/bootstrap.sh root@<ip>:/root/
   ssh -t root@<ip> bash /root/bootstrap.sh          # prompts for GHCR user + read-only PAT
   ```

   The PAT is a GitHub fine-grained/classic token with **only `read:packages`** — generated by the
   owner, never written down in the repo. For automation you *can* pass it via env
   (`ssh root@<ip> 'GHCR_USER=<u> GHCR_TOKEN=<pat> bash -s' < scripts/hetzner/bootstrap.sh`), but
   the inline assignment **lands in your local shell history** — prefer the prompt for hand runs.
   Without env vars and without a TTY the script skips the GHCR login with a warning and the rest
   still completes.

   **First converge on the hand-hardened Phase-0 pair:** before the first run, `diff` the live
   `/etc/ssh/sshd_config.d/00-hardening.conf` and `/etc/fail2ban/jail.local` against what the
   script writes — bootstrap **overwrites** them (converges to the repo version), so any extra
   hand-tuned directive there is discarded. Fold anything worth keeping into the script first.
4. **Stack files:** copy `compose.hetzner.yaml` + `.docker/hetzner/` to `/opt/lotro/` (as
   `deploy`), assemble the box's `.env` from `.env.hetzner.example` (every variable, its source of
   truth and its rotation command: §Secrets and env vars above), `chmod 600` it.
5. **Bring-up (staging box first as the rehearsal):** as `deploy`, on each box:

   ```bash
   cd /opt/lotro     # .env is picked up automatically; COMPOSE_PROJECT_NAME comes from it
   docker compose -f compose.hetzner.yaml pull
   docker compose -f compose.hetzner.yaml up -d
   docker compose -f compose.hetzner.yaml logs -f migrator caddy   # one-shot migration + ACME
   ```

   Then `scripts/smoke.sh` against the public origins from an external network — remember
   `GET / -> 200` proves nothing (house rule); smoke's fingerprint leg is the real check.

## Disaster recovery

Each box is **fully disposable** — nothing on it is the source of truth for anything:

| What | Lives | Recovery |
|---|---|---|
| Databases | Neon (prod + staging, PITR + MIGR-04 snapshots) | nothing to do |
| Images | GHCR (built by `cd.yml`) | nothing to do |
| Config | this repo (`compose.hetzner.yaml`, Caddyfile) | scp again |
| Secrets | `/opt/lotro/.env` per box — owner's backup + every value re-mintable (§Secrets and env vars) | restore or re-mint |
| TLS certs | Caddy volume; Let's Encrypt re-issues | automatic on first bring-up |

Recovery = **new VPS → bootstrap.sh → scp stack files → restore/re-mint the box's `.env` →
`docker compose up -d` → re-point DNS A records**. Hetzner backups (prod box) are a shortcut for
the same outcome, not a dependency.

## Gotchas

- **Docker bypasses ufw for published ports** (it programs iptables directly). Our stacks publish
  only Caddy's 80/443 — which ufw allows anyway. Never publish another service's port "just to
  debug"; exec into the network instead (`docker compose exec caddy wget -qO- http://tms-api:8080/health`).
- **sshd config precedence:** sshd honours the *first* occurrence of a keyword, and
  `/etc/ssh/sshd_config.d/` is included at the top in lexical order. Bootstrap's hardening lives
  in `00-hardening.conf` (same file Phase 0 wrote by hand — bootstrap converges it) precisely so
  it wins over cloud-init's `50-cloud-init.conf` — don't rename it to a higher number.
- **ACME fails until DNS propagates** — that's retry-resolved, not an error to fix. Start DNS
  before bring-up; Caddy keeps retrying on its own.
- **First cold hit after Neon scale-to-zero** can race auth's 20 s connection-open against Neon's
  ~31 s resume (known pre-existing issue; connection strings carry `Timeout=60`). The always-on
  box shrinks the window but does not fix the bug.
- In Production, OpenIddict rejects plain-HTTP — every authority/issuer URL must be the public
  `https://` origin (same rule as the parity stack; details in `docs/deployment/runbook.md`).
- **4 GB box + ~9 containers → add swap on the prod box** (ADR-0034 §1 amendment: the fleet is
  CX23/4 GB, not the CX32/8 GB the ADR argued for). Hetzner images ship without swap. One-time, as
  root — safe on a running box (no restart), idempotent by the guards:

  ```bash
  swapon --show | grep -q . || { fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile; }
  grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  ```

  Deliberately NOT in `bootstrap.sh`: swapfile creation + `/etc/fstab` edits can't be faithfully
  proven in the container idempotency harness, and shipping unverified fstab edits is worse than a
  runbook step the operator runs and eyeballs. Candidate to fold into bootstrap once verified on a
  real box (HETZ follow-up).
- **Container log rotation is unbounded by default** (json-file driver, small VPS disk). Cap it at
  the compose level (`logging: { driver: json-file, options: { max-size: "10m", max-file: "3" } }`
  per service) in a HETZ follow-up — NOT via `/etc/docker/daemon.json` on the live boxes, since a
  daemon-config change restarts dockerd and bounces every running container.
- **Compose service KEYS must be globally unique on the shared network.** Compose registers the
  service key itself as a Docker DNS alias on top of `container_name` — a guest stack (TKS) whose
  compose also says `frontend:`/`auth-api:` collides with ours, and Caddy's
  `reverse_proxy frontend:8080` then round-robins into the WRONG stack (2026-07-12 prod incident:
  lotro-translator.pl served uratujkota.pl). Every TKS service key carries the `tks-` prefix;
  any new stack joining this network must prefix its keys the same way. Detection:
  `docker inspect -f '{{range $k,$v := .NetworkSettings.Networks}}{{$v.Aliases}}{{end}}' <ctr>` —
  no alias may appear on two containers.
