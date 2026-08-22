# ADR-0050: The observability backend runs on the staging box, and every box pushes to it

**Status:** Accepted
**Date:** 2026-08-22
**Decision-makers:** Solo maintainer
**Related:** #709 (OBS-02, this decision), epic #707 (OBS-00), #708 (swap — landed), #710/#711/#712/#713/#714 (the children this constrains), koniecdev/TheKittySaver#508 (sister epic), ADR-0034 + its #506 amendment (two segregated networks, Caddy is the only shared component), ADR-0027 (daily health ping), `compose.hetzner.yaml`, `.docker/hetzner/Caddyfile`, `scripts/hetzner/deploy.sh`

## Context

ADR-0034 moved the stack off Azure and deleted the whole alerting layer with it: the four Azure
Monitor alert rules, Log Analytics and Application Insights. The runbook states the gap plainly —
"no metric alerting and no trace backend — a crash-loop between two daily pings is invisible unless
a user reports it." Epic #707 closes that gap with an open-source Grafana stack on the boxes we
already pay for. The owner ruled on 2026-08-21 that a third box and Grafana Cloud are both out.

Code and measurement facts that constrain the choice:

- **Both boxes are 3.7 GiB CX23s and both are nearly idle.** Measured 2026-08-21 over ssh —
  prod: 2.1 GiB available, 7 containers at 1.02 GiB RSS, load 0.01, 28 GB disk free; staging:
  2.2 GiB available, 8 containers at 0.85 GiB RSS, load 0.61, 24 GB disk free. Prod gains one more
  container (RabbitMQ, ~115 MiB) at its next promotion. #708 has since added 2 GiB of swap to both,
  so a spike now slows the box down instead of OOM-killing a container at random.
- **Caddy is the only component that sits on more than one network** (`compose.hetzner.yaml`
  `networks:`; ADR-0034's #506 amendment). Our stack is alone on `default` (10.60.0.0/24), the guest
  TheKittySaver stack is alone on `tks` (10.61.0.0/24), and the apps trust `X-Forwarded-*` from
  Caddy's `10.60.0.100/32` only. This is the finding that closed the cross-stack pivot; anything the
  observability stack adds must apply that rule, not carve an exception out of it.
- **`ufw` denies everything inbound except 22/80/443, and Docker's iptables rules bypass it for
  published ports** (`scripts/hetzner/bootstrap.sh:286`). Only Caddy publishes ports today. Any new
  published port is a hole in a firewall that then means nothing.
- **`compose.hetzner.yaml` is a CD artifact, not box state.** `deploy.sh` overwrites it on every
  rollout and runs `up -d --remove-orphans`, and the file's own header states its invariant: "One
  file serves BOTH environments … differentiated ONLY by the env file". A backend on one box and an
  agent on the other cannot be expressed there without breaking that.
- **The apps already put the trace id in the log line.** `CompactJsonFormatter` v3 plus Serilog 4
  write `@tr`/`@sp` from `Activity.Current`, and `AddOpenTelemetry().WithTracing(...)` registers the
  ASP.NET Core listeners whether or not an exporter is configured
  (`TranslationSystem.API/Program.cs:85`). Log-to-trace correlation does not depend on a trace
  backend existing.
- **`OTEL_EXPORTER_OTLP_ENDPOINT` is one switch for three signals.** The same
  `if (!string.IsNullOrWhiteSpace(otlpEndpoint))` turns on the Serilog OTLP **log** sink
  (`Program.cs:60`) and `UseOtlpExporter()` for traces and metrics (`Program.cs:100`). Setting it
  while an agent also tails Docker logs puts every app log line into Loki twice.
- **Caddy sees every response status and logs none of them.** `.docker/hetzner/Caddyfile` has no
  `log` directive, so there is no access log today — but turning one on gives a per-vhost 5xx rate
  for both projects and both environments, with no app instrumentation at all.

## Decision

### 1. The staging box hosts the backend; prod ships to it

Grafana, Loki and Prometheus run on **lotro-staging**. Prod runs the agent only.

The reason is the failure mode the ticket asks this ADR to name: **a co-hosted backend dies with the
box it observes.** Putting it on prod means a prod outage takes down the site and the evidence at the
same moment, and the logs from the minutes before the failure sit on a box you cannot reach. Putting
it on staging inverts that: when prod dies — the outage that matters — the backend is still up and
still holds prod's telemetry right up to the last second. The only outage that costs visibility is
staging's own, and a staging outage is not an incident.

Staging also absorbs the extra memory, disk churn and log volume, which is load on the box whose
degradation is free.

**What is lost when the observing box dies:** everything. A dead staging box means no dashboards, no
alert evaluation and no alert delivery — including alerts about prod. Three things compensate, and
none of them run on a box:

- the **daily health ping** (ADR-0027, `.github/workflows/health-ping.yml`) stays, unchanged. It runs
  on GitHub, probes the prod origins on the deep `/health`, and is the one check that proves the
  database is reachable;
- the **post-deploy smoke** stays, with automatic rollback on red;
- Grafana gets a **"no data from prod"** absence alert, so a silent shipping path is itself a signal
  rather than a quiet gap in a graph (#713).

The residual gap is honest and small: between two health pings, a staging box that is down means a
prod incident is not alerted. That is the same position we are in today, so the first cut cannot make
it worse — it can only fail to improve it during a staging outage.

### 2. The observability stack is its own compose project, on its own network

It ships as `compose.observability.yaml` in this repo, deployed to `/opt/obs` on each box and brought
up as a separate compose project. It is **not** a profile inside `compose.hetzner.yaml`.

Reasons, in order of weight: CD overwrites `compose.hetzner.yaml` and recreates every service in it
on each rollout, so the backend's lifetime would be tied to the app rollout; that file's stated
invariant is one file for both boxes, which a here-but-not-there topology breaks; and a separate
project makes #710's "killing the observability stack leaves every app container running" true by
construction rather than by care. (A profile would probably survive `--remove-orphans` — verified on
Compose v5.4.0 / Docker 29.7.2, which does not remove profile services — but the design does not rely
on that either way, and the box runs Ubuntu's `docker-compose-v2` at an unpinned version.)

A third network `obs` (`10.62.0.0/24`, `name: ${COMPOSE_PROJECT_NAME}_obs`) is declared in
`compose.hetzner.yaml` next to `tks`, and the observability project joins it as `external`. Caddy
attaches to it with a pinned `10.62.0.100`. This is the `tks` pattern applied a second time, not a
new idea.

### 3. Caddy stays the only component on more than one network

No observability container joins `default` or `tks`. In particular:

- **Nothing scrapes an app container.** A Prometheus or an agent that could reach `auth-api:8080` is
  the cross-stack pivot #506 closed, reintroduced under a friendlier name. Host- and container-level
  metrics come from `/proc` and the Docker socket, which need no app network at all.
- **The backend never reaches into an observed box.** The boundary is crossed by **pushes only**:
  prod's agent writes out to the backend. Nothing on the backend box holds a credential for prod, or
  a route into it. So even the objection to §1 — prod's telemetry now lives on the box that takes
  every push — buys an attacker prod's logs and no path to prod.
- When the OTLP leg lands (§6), an app reaches the local agent **through Caddy**, on the network
  alias it already uses, exactly like the auth back-channel does today. The agent stays on `obs`.

### 4. The transport is one more vhost on the Caddy that already exists

`obs.lotro-translator.pl`, A record pointed at the **staging** box, `DOMAIN_OBS` in the box `.env`
(prod keeps an inert `*.localhost` default, the same way `TKS_DOMAIN_*` already does). Caddy does
ACME for it like every other origin.

Two route groups on that vhost:

| Path | Who | Gate |
|---|---|---|
| `/loki/api/v1/push`, `/api/v1/write` | prod's agent | `remote_ip` = the other box's static public IP, `/32` (from `OBS_ALLOWED_PUSH_IP`) **and** `basic_auth` with a bcrypt hash from the box `.env` |
| everything else | Grafana | Grafana's own login; `GF_AUTH_ANONYMOUS_ENABLED=false` |

The vhost stamps `X-Robots-Tag: noindex, nofollow` explicitly — it does not import `seo_headers`,
which is `all` on prod.

**Why this does not weaken #506.** #506 is about who may talk to whom *inside* a box, and it is
enforced by network topology plus a `/32` trust pin. This change adds no container to a network it
was not already alone on, moves no alias, and widens no `KnownNetworks` value. What it adds is one
more public hostname on the component that is already the single public ingress and already the
single shared trust boundary. The ingest routes are narrowed twice over — to one source address and
to one credential — so the surface is public in name only. WireGuard would be a stronger posture and
was rejected on cost (§B below); an ssh tunnel was rejected as fragile.

The backend box's own agent does not use this path. It writes to `loki:3100` on the `obs` network,
which never leaves the box.

### 5. Memory budget: 1.5 GiB of ceilings on the backend box, 256 MiB on the other

Every component gets a hard `mem_limit`. The point of putting monitoring on shared hardware is that
the monitoring must never be what takes the site down, and a limit is the only thing that makes that
a property rather than a hope.

| Component | `mem_limit` | Expected RSS | staging | prod |
|---|---|---|---|---|
| Grafana | 256 MiB | ~120 MiB | ✓ | — |
| Loki | 512 MiB | ~200 MiB | ✓ | — |
| Prometheus | 512 MiB | ~250 MiB | ✓ | — |
| Alloy | 256 MiB | ~120 MiB | ✓ | ✓ |
| **Total ceiling** | **1536 MiB** / **256 MiB** | ~690 MiB / ~120 MiB | | |

Against the measurement: staging has 2.2 GiB available, so **the stack fits even with every ceiling
hit at once**, with ~0.7 GiB to spare and before touching the 2 GiB of swap #708 added. Prod has
2.1 GiB available minus ~115 MiB for the RabbitMQ container it gains at its next promotion, and the
agent's 256 MiB ceiling leaves ~1.75 GiB.

The expected column is a prediction, not a measurement. #710 records the real RSS after a week and
this table gets corrected from that, rather than trusted.

### 6. Tempo and the OTLP leg are both deferred to a second cut

The first cut collects **logs from the Docker socket** and **host and container metrics**. It sets no
`OTEL_EXPORTER_OTLP_ENDPOINT`, adds no app instrumentation, and runs no trace backend.

This works because all four alert rules ADR-0034 deleted are reachable without app telemetry:

| Alert | Source in the first cut |
|---|---|
| crash-loop | container restart count, from the Docker socket |
| 5xx rate, per project and environment | Caddy's access log, per vhost — the edge view, and it covers TheKittySaver identically |
| memory / CPU / swap saturation | host metrics from `/proc` |
| error-level log rate spike | the log lines already in Loki |

Tempo is the heaviest component and the least urgent — with no users, traces buy the least of
anything in the epic. Deferring it is what keeps §5's "fits even at every ceiling" property. Nothing
is lost permanently: the trace id is already in every log line, so a Grafana derived field links it
to a trace the day Tempo lands, retroactively for whatever logs are still retained.

The OTLP leg goes with it, because with Tempo out it would carry metrics we already have from a
cheaper source, while costing an app-facing route through Caddy and the duplicate-log problem in §7.

### 7. Logs have exactly one path: the Docker socket

When the OTLP leg does land, it must not double-ship logs. The single path is **stdout tailing**, and
the Serilog OTLP log sink stays off, because stdout is the strictly larger set: it catches a crash
before the logging pipeline is up, it catches containers that speak no OTLP (Caddy, RabbitMQ, the
migrator, and all of TheKittySaver today), and it still carries the trace id.

Today the two are one switch (`Program.cs:60` and `:100` share an `if`), so the OTLP leg cannot land
until the log sink has its own. That is a small code change in three `Program.cs` files and it needs
its own ticket — it is a prerequisite of #712, not a detail of it.

### 8. Retention: Prometheus caps itself, Loki cannot, and the difference is stated

- **Prometheus:** `--storage.tsdb.retention.time=15d` **and** `--storage.tsdb.retention.size=4GB`.
  Whichever hits first wins, and the size cap is enforced by the store, so Prometheus can never
  exceed 4 GB no matter what happens upstream.
- **Loki:** `retention_period: 336h` (14 days), with `compactor.retention_enabled: true` **and**
  `delete_request_store` set. Both are required, and they fail differently: `retention_enabled`
  defaults to false, so a `retention_period` on its own is a **silent** no-op — a configured cap that
  deletes nothing until the disk fills. A missing `delete_request_store` is the loud one; the
  compactor refuses to start. Only the silent failure is dangerous, so #710 verifies retention by
  reading the rendered config, not by trusting the value.
- **Loki has no size-based retention**, so its inflow is bounded instead: `ingestion_rate_mb: 1`
  (burst 2), `per_stream_rate_limit: 128KB` (burst 512KB), and a `max_global_streams_per_user` cap.
  The per-stream limit is what stops one crash-looping container from swamping the box in minutes.
- **The backstop is a free-space alert** at 20% and again at 10% (#713), because the two bullets
  above are not the same guarantee and pretending otherwise is how a disk fills.

Stated plainly: at the measured log volume of this fleet, 14 days of logs is a few hundred megabytes
against 24 GB free, and Prometheus is hard-capped at 4 GB — call the whole budget under 5 GB. The
rate limits bound the pathological case; they do not turn the retention window into a byte ceiling.
If a future traffic level makes that expectation unsafe, the lever is **a shorter retention**, not a
bigger box.

## Consequences

### Positive

- A prod outage no longer destroys its own evidence. The backend survives on another box and the
  logs and metrics from the minutes before the failure are readable while prod is still down.
- Nothing new is published, no firewall rule changes, no daemon is added to either box. The transport
  is a hostname on a proxy that was already the single ingress and is already operated.
- #506's rule holds without an exception: Caddy remains the only component on more than one network,
  and the new network is the third instance of a pattern already in the file.
- The cross-box direction is push-only, so the box holding the telemetry has no route into the box
  that produced it.
- The first cut delivers all four deleted alert rules with zero application changes, which means no
  new deploy risk on the apps and no `--no-restore`-class surprise in an image.
- The boxes stay disposable. Dashboards, alert rules and datasources are provisioned as code in this
  repo, so a rebuilt box gets everything back except history.

### Negative / Accepted Trade-offs

- **Prod's telemetry lives on the lower-trust box.** Staging takes every push and runs unreviewed
  code. A staging compromise reads prod's logs — which can contain user e-mail addresses. It gains no
  path into prod (§3), and the same owner and the same hardening cover both boxes, but this is a real
  reduction and it is accepted deliberately.
- **A dead staging box means no alerting at all, for either environment.** The daily health ping and
  the CD smoke are the off-box compensation, and they are coarse.
- **No traces in the first cut.** Two acceptance criteria are written assuming otherwise (#707's
  "traces … land in a backend", #712's "a request … produces a trace visible in Grafana") and must be
  rewritten to logs plus metrics, with the trace view moved to the second cut.
- **#712 is no longer the next step after #711.** The order becomes #710 → #711 → #713 → the log-sink
  switch → #712 with Tempo. #712's "one-variable change" claim is also not quite true: it needs the
  §7 code change first.
- **Losing the staging box loses telemetry history.** Accepted — telemetry is not a source of truth,
  and the DR table's "every box is fully disposable" line survives with a new row saying exactly this.
- **The observability stack is a second thing to deploy**, outside CD, with its own bring-up steps in
  the runbook. That is the price of not coupling it to the app rollout.

## Alternatives Considered

### A. The backend on prod

The ticket's own lean, and prod has more free disk (28 GB vs 24 GB). **Rejected.** It accepts the
exact failure mode this ADR exists to avoid: the box whose outage matters is the box that would be
holding the evidence, unreachable at the moment it is needed. It also points the dependency the wrong
way, making prod absorb staging's log flood and disk churn.

### B. WireGuard between the two boxes

The strongest posture — nothing on the public internet, telemetry never touches the TLS terminator.
**Rejected on cost, not on merit.** It adds a daemon per box, a UDP port in `ufw`, key material to
generate and rotate, an MTU class of bug, and a second network fabric to reason about next to the
three Docker networks — all of it in a new `bootstrap.sh` leg that has to be right on a box that is
awkward to test. The marginal risk it removes is an HTTPS endpoint narrowed to one source IP and one
credential. Reopen this if the fleet grows past two boxes, or once real user data flows through the
logs.

### C. An ssh tunnel

No new daemon, reuses the deploy key. **Rejected.** A long-lived tunnel needs `autossh` or a systemd
unit to survive, and when it dies it dies silently — telemetry stops with no signal, which is the one
failure an observability transport must not have.

### D. A third box for the backend

**Rejected by the owner, 2026-08-21.** The measurement says the existing boxes have the room; a third
CX23 is a recurring cost for capacity we already own.

### E. Grafana Cloud free tier

**Rejected by the owner, 2026-08-21.** It solves the "dies with the box it observes" problem
outright, but it re-introduces the managed-vendor dependency ADR-0034 just spent an epic removing,
ships user-adjacent logs to a third party, and its free tier is a retention and series cap that
becomes a migration the day it is exceeded.

### F. Tempo in the first cut

**Rejected** — see §6. It is the heaviest component and the least urgent, and including it puts the
all-ceilings-at-once total at roughly the measured headroom, so the stack would fit only because
#708 added swap.

### G. An observability profile inside `compose.hetzner.yaml`

The shape #710 currently proposes. **Rejected** — see §2. The file is a CD artifact that is
overwritten and recreated on every rollout, and its one-file-serves-both-boxes invariant cannot
express a backend on one box only.

## Implementation Notes

- `compose.hetzner.yaml` — declare the `obs` network (`10.62.0.0/24`,
  `name: ${COMPOSE_PROJECT_NAME}_obs`) next to `tks`; attach Caddy with `ipv4_address: 10.62.0.100`
  and the `${DOMAIN_OBS}` alias. No app service changes.
- `.docker/hetzner/Caddyfile` — the `{$DOMAIN_OBS}` vhost with the two route groups of §4, the
  explicit `noindex, nofollow` header, and a `log` directive on the LOTRO and TKS vhosts so the 5xx
  alert has a source.
- `.env.hetzner.example` — `DOMAIN_OBS`, `OBS_ALLOWED_PUSH_IP`, `OBS_PUSH_BASIC_AUTH_HASH`,
  `GF_SECURITY_ADMIN_PASSWORD`, with the prod/staging split documented the way the file already does
  for `DOMAIN_*` and `XROBOTS`.
- `compose.observability.yaml` (new) — the backend services plus the agent, `mem_limit` on each per
  §5, joining `${COMPOSE_PROJECT_NAME}_obs` as `external`. Deployed to `/opt/obs`, owned by `deploy`
  (the `/opt/lotro` root-ownership gotcha applies here too).
- `scripts/hetzner/bootstrap.sh` — create `/opt/obs` alongside `/opt/lotro` and `/opt/tks`.
- Three `Program.cs` files (`TranslationSystem.API`, `AuthSystem.API`, `Frontend`) — split the
  single `if (!string.IsNullOrWhiteSpace(otlpEndpoint))` so the Serilog OTLP log sink has its own
  switch (§7). Prerequisite of #712; needs its own ticket.
- `docs/deployment/runbook.md` (#714) — replace the "Observability & monitoring" gap section, add the
  `obs` network and the third `/opt` directory to the topology, add the new env vars to the matrix,
  and add a DR row saying telemetry history dies with the staging box on purpose.
- Ticket edits this ADR forces: #707 and #712 acceptance criteria (traces out of the first cut),
  #710 (separate compose project, not a profile), #711 (no app-container scraping; Caddy access log
  as the 5xx source), and the epic's ordering.

## References

- Epic #707 (OBS-00) — the measured box state this budget is built on; children #708–#714
- koniecdev/TheKittySaver#508 — the sister epic; TKS ships into this same backend, labelled apart
- ADR-0034 and its 2026-07-13 amendment (#506) — the two-network topology and the Caddy-only trust
  boundary this decision extends rather than excepts
- ADR-0027 — the daily health ping, which stays as the off-box backstop
- `docs/deployment/runbook.md` — "Observability & monitoring" (the gap), "Disaster recovery",
  "Gotchas" (`/opt/lotro` ownership, Docker publishing past `ufw`)
- Grafana Loki retention: https://grafana.com/docs/loki/latest/operations/storage/retention/
- Prometheus storage limits: https://prometheus.io/docs/prometheus/latest/storage/
