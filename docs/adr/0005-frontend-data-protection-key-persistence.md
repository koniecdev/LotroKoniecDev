# ADR-0005: Persist Frontend Data Protection keys to a shared filesystem volume

**Status:** Accepted
**Date:** 2026-06-15
**Decision-makers:** Solo maintainer
**Related:** ticket #40 (M3-06 — Frontend Dockerfile + join compose), M3-01 #138 (lifted OIDC RP
infra), M3-02 #139 (auth session UX), TheKittySaver ADR-0006 (the lifted decision — same
data-protection posture), `Infrastructure/Auth/DataProtectionDependencyInjectionExtensions.cs`
(the registration this ADR records)

## Context

`LotroKoniecDev.Frontend` (Blazor SSR) relies on ASP.NET Core **Data Protection** for every piece of
client-held crypto material it issues:

- the auth cookie `.lotrokoniecdev.auth`
  (`AuthenticationDependencyInjectionExtensions.cs`), which carries the OIDC tokens via
  `SaveTokens = true`;
- the **antiforgery** tokens behind every mutation form — the Frontend is pure SSR, so every
  `<form method="post">` surface (logout in `NavMenu.razor`, save / approve in `Editor.razor`)
  depends on them;
- the transient **OIDC correlation + nonce** cookies written during the login handshake.

All three are encrypted/signed with the Data Protection keyring. The framework default keyring is
**ephemeral and process-local** in a container: its default location lives under a home directory
that is not persisted across `docker run` / image rebuild, so every redeploy mints a brand-new
keyring. Past a single instance the same fault is structural — each replica generates its own
keyring, so a cookie minted by replica A is undecryptable by replica B.

A rotated/forked keyring breaks all three surfaces at once:

| Surface | Failure when the keyring is not shared/persisted |
|---|---|
| `.lotrokoniecdev.auth` | Existing cookie can't be unprotected → silent mass logout on every deploy; load-balanced requests randomly 302 to login |
| Antiforgery | POST tokens minted under the old key fail validation → every form submit 400s until the page is reloaded |
| OIDC correlation/nonce | A login that starts on one key/replica and finishes on another fails with "Correlation failed" — login itself becomes flaky |

Two constraints frame the choice:

- **The Frontend is deliberately database-less.** Its csproj references no EF, no Npgsql, no Redis;
  its contract with the backend is *typed HTTP clients, nothing else* (M3-01). Introducing a DB or
  Redis purely to store keys would breach that boundary.
- **Dev already persists keys by accident.** When the Frontend runs on the host via `dotnet run`
  its default keyring at `~/.aspnet/DataProtection-Keys` survives restarts. The gap is exclusively
  the containerized/multi-replica deployment — which is precisely what ticket #40 adds (the
  Frontend Dockerfile + its `compose.yaml` service).

This is the lifted twin of TheKittySaver ADR-0006; the registration code is a near 1:1 lift. It is
**not** a contested business decision — it records the deployment posture the lifted infra already
implements, made concrete now that #40 ships the container.

## Decision

### 1. Persist the keyring to the filesystem; path supplied by configuration

`AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(path))` is registered when a keyring
path is configured (`DataProtection:KeyRingPath`). The path is mounted to a Docker/host **volume**,
so the keyring outlives container recreation. Filesystem is chosen over Redis/DB because it adds no
new runtime component and no in-process data dependency to a database-less app.

### 2. Pin a stable application name

`SetApplicationName("LotroKoniecDev.Frontend")` in **every** environment. The application name is
part of the Data Protection purpose/isolation; its default is the content-root path, which differs
between host-dev and the container (`/app`) and is identical across replicas only by luck. Pinning
one stable name is what makes a key created by one instance/path usable by another — without it a
shared volume still yields per-path key isolation and the cookies stay mutually unreadable.

### 3. Dev falls back to the framework default; only containers mount a path

When `DataProtection:KeyRingPath` is empty, `PersistKeysToFileSystem` is **not** called — the
host-dev default location already persists, and hardcoding a container path into dev would be wrong.
The dev `compose.yaml` runs the Frontend under `ASPNETCORE_ENVIRONMENT=Development` and does **not**
mount a keyring, so it intentionally uses the in-container ephemeral default: acceptable for a local
smoke stack with no real sessions to preserve.

### 4. Fail fast outside Development when the path is missing

Outside Development, an empty `DataProtection:KeyRingPath` throws at startup. An ephemeral keyring in
production is never intended — it silently invalidates every auth cookie / antiforgery token / OIDC
correlation cookie on each deploy and makes multi-replica impossible. Loud over silent: the
deployment is forced to mount a shared volume and point the keyring at it.

### 5. The Dockerfile prepares the mount point but declares no `VOLUME`

`src/Frontend/LotroKoniecDev.Frontend/Dockerfile` creates `/keys` owned by the non-root `app` user
as the conventional mount point and documents the requirement. It deliberately omits a `VOLUME`
instruction: an anonymous volume would silently mask a missing real mount and re-introduce the
ephemeral-key failure under a false sense of safety. A named volume covers single-host
restart/scale; multi-host needs a `ReadWriteMany` backend (NFS/EFS/Azure Files).

## Consequences

### Positive

- Live sessions survive deploys and scale-out — no mass logout, no flaky login, no form 400s from a
  rotated keyring.
- No new runtime component: the database-less Frontend stays database-less.
- The non-dev startup guard makes a mis-deploy fail immediately and legibly instead of degrading in
  production.

### Negative / Accepted trade-offs

- The deployment must provision and mount a persistent volume and set `DataProtection:KeyRingPath` —
  a one-line orchestration requirement, enforced by the startup guard.
- Multi-host deployments need a shared (RWX) filesystem; a plain per-host named volume is
  single-host only. Documented in the Dockerfile and here; revisit if/when multi-host is real
  (YAGNI today).
- The dev compose keyring is ephemeral by choice — restarting the `frontend` container logs out any
  in-progress dev session. Acceptable for a local smoke stack.

## Alternatives considered

- **Store keys in Redis / a database.** Rejected — adds a runtime component and an in-process data
  dependency to an app whose entire contract is "HTTP clients, nothing else"; filesystem + a mounted
  volume achieves the same persistence with zero new infrastructure.
- **Leave the framework default.** Rejected — ephemeral and process-local in a container; breaks all
  three crypto surfaces on every deploy and is structurally impossible across replicas.
- **Declare a `VOLUME /keys` in the Dockerfile.** Rejected — an anonymous volume masks a missing
  real mount, so a misconfigured deploy would *look* fine while still minting a fresh keyring per
  container. Preparing the directory without `VOLUME` keeps the missing-mount failure visible.

## References

- TheKittySaver `docs/adr/0006-frontend-data-protection-key-persistence.md` — the lifted decision
  (same posture, near-identical registration code)
- `src/Frontend/LotroKoniecDev.Frontend/Infrastructure/Auth/DataProtectionDependencyInjectionExtensions.cs`
  — the registration (stable application name, conditional `PersistKeysToFileSystem`, non-dev guard)
- `src/Frontend/LotroKoniecDev.Frontend/Settings/DataProtectionSettings.cs` — the `KeyRingPath`
  option and its semantics
- `src/Frontend/LotroKoniecDev.Frontend/Dockerfile` — the `/keys` mount point + the "no `VOLUME`"
  rationale
- Ticket #40 (M3-06); M3-01 #138 (lifted OIDC RP infra)
