#!/usr/bin/env bash
# Roll the /opt/lotro stack to a given GHCR image tag. Runs ON a Hetzner box as the `deploy` user
# (ADR-0034, epic #486, ticket #490) — CD (.github/workflows/deploy.yml) scp's this file next to the
# stack and drives it over ssh, and an operator runs the very same command by hand to deploy or roll
# back (docs/deployment/runbook.md → "Continuous deployment (CD over ssh)").
#
#   IMAGE_TAG=sha-1a2b3c4 bash /opt/lotro/deploy.sh
#
# What it does, in order:
#   1. records the CURRENT tag and prints it as `PREVIOUS_IMAGE_TAG=<tag>` — CD captures that line
#      and feeds it straight back into this script to roll back a red smoke.
#   2. validates the composed config AND the Caddyfile, then pulls, and — when CD passes
#      EXPECTED_DIGESTS — asserts the pulled bytes are the exact digests whose build provenance CD
#      verified. GHCR tags are mutable; this closes the gap between the attestation gate on the
#      runner and the image that actually starts here.
#   3. THE MIGRATION GATE: runs the migrator as a ONE-OFF container (`compose run`) and only proceeds
#      when it exits 0 — see the warning below for why this cannot be left to `depends_on`.
#   4. `up -d` — the apps are recreated only now, on an already-migrated schema.
#   5. reloads Caddy: its Caddyfile is a bind mount, and compose does not hash mounted file CONTENT,
#      so without this a changed Caddyfile would sit on disk unapplied until someone restarted it.
#   6. writes IMAGE_TAG into the box `.env` — LAST, and only on success, so the file always describes
#      what is actually running. A later bare `docker compose up -d` by an operator then re-asserts
#      the deployed tag instead of silently reverting to whatever the file said before.
#
# WARNING — do NOT collapse step 3 back into `up -d` and lean on compose.hetzner.yaml's
# `depends_on: { migrator: { condition: service_completed_successfully } }`. That condition gates the
# START of the apps, not their CREATION: `compose up` runs a recreate pass over ALL services first
# and only then starts them in dependency order. With a new image tag (i.e. every deploy) the old app
# containers are therefore destroyed BEFORE the migrator runs, so a failed migration leaves the
# environment DOWN (apps `created`, never started) instead of "still serving the old release" —
# verified against Docker 29.6.1 while reviewing #490. Running the migrator as its own gate is what
# makes a bad migration a no-op for the running site.
#
# There is no blue/green here (one 4 GB box, three apps — a second live set does not fit): `up -d`
# recreates containers in place, so a deploy is a few seconds of downtime per app (ADR-0034 §3).
# Rollback is this same script with the previous tag.
#
# Deliberately NO .ps1 twin: the script-twins rule covers cross-platform dev machines; this runs only
# on the Linux server (same call as scripts/hetzner/bootstrap.sh).

set -euo pipefail

STACK_DIR="${STACK_DIR:-/opt/lotro}"
COMPOSE_FILE="${COMPOSE_FILE:-compose.hetzner.yaml}"
IMAGE_TAG="${IMAGE_TAG:-}"
# Space/newline-separated full refs (`ghcr.io/<ns>/<image>@sha256:…`) that the pulled images MUST
# match. Empty (a hand-run deploy) simply skips the assertion — CD always passes them.
EXPECTED_DIGESTS="${EXPECTED_DIGESTS:-}"
# Unused images are swept after this long. Every deploy leaves 4 superseded per-commit images behind
# and they are NOT dangling (they keep their sha-<short> tag), so a dangling-only prune would never
# reclaim a byte on a 40 GB disk. Keeps ~2 weeks of tags around to roll back to instantly.
PRUNE_UNUSED_OLDER_THAN="${PRUNE_UNUSED_OLDER_THAN:-336h}"

log() { printf '\n==> %s\n' "$*"; }

if [ -z "$IMAGE_TAG" ]; then
    echo "deploy.sh: IMAGE_TAG is required (e.g. IMAGE_TAG=sha-1a2b3c4 bash $0)." >&2
    exit 2
fi

# The tag is interpolated into compose refs and written to .env — keep it to what a GHCR tag may
# actually contain, so nothing downstream can be talked into interpreting it as something else.
case "$IMAGE_TAG" in
    *[!A-Za-z0-9._-]* | '')
        echo "deploy.sh: IMAGE_TAG '$IMAGE_TAG' is not a valid image tag ([A-Za-z0-9._-] only)." >&2
        exit 2
        ;;
esac

cd "$STACK_DIR" || { echo "deploy.sh: stack dir $STACK_DIR not found — is the box bootstrapped?" >&2; exit 2; }

for required in "$COMPOSE_FILE" .env; do
    if [ ! -f "$required" ]; then
        echo "deploy.sh: $STACK_DIR/$required is missing (runbook → '(Re)provisioning a box')." >&2
        exit 2
    fi
done

# Every compose call below resolves ${IMAGE_TAG} from THIS exported value, which outranks the .env
# file. That is deliberate: the .env is only rewritten at the very end, on success, so a roll that
# dies anywhere before that leaves the box's declared state and its running containers BOTH on the
# old tag — no drift to repair, and an operator's bare `docker compose up -d` still does the right
# thing.
export IMAGE_TAG

compose() { docker compose -f "$COMPOSE_FILE" "$@"; }

# Any failure below leaves the box mid-roll; the logs of the migrator and the apps are what an
# operator (or the CD log) needs to see, so print them before dying.
dump_logs_on_failure() {
    status=$?
    if [ "$status" -ne 0 ]; then
        log "Deploy FAILED (exit ${status}) — container state and recent logs:"
        compose ps || true
        compose logs --tail=80 --no-color migrator auth-api tms-api frontend caddy || true
    fi
    exit "$status"
}
trap dump_logs_on_failure EXIT

previous_tag="$(sed -n 's/^IMAGE_TAG=//p' .env | head -1)"
log "Rolling ${STACK_DIR} to IMAGE_TAG=${IMAGE_TAG} (was: ${previous_tag:-<unset>})"
# CD greps this line to learn what to roll back TO — keep the format stable, and keep it BEFORE
# anything can fail, so a half-way failure still tells CD where to return to.
printf 'PREVIOUS_IMAGE_TAG=%s\n' "$previous_tag"

log "Validating the composed configuration"
compose config --quiet

# The Caddyfile is only ever read by Caddy at (re)load, so a broken one would otherwise sit on disk
# undetected until the reload at the end of this script — or, worse, until the next reboot. Validate
# it up front, while nothing has been touched: the running Caddy keeps serving its current config.
#
# It MUST go through compose (not a bare `docker run --env-file .env`): the Caddyfile's
# {$TKS_DOMAIN_*} placeholders are fed by compose's `${TKS_DOMAIN_APP:-tks-app.localhost}` defaults,
# which live in compose.hetzner.yaml and NOT in .env — a box without the TheKittySaver stack leaves
# those variables empty, and an empty site key makes Caddy read the block as a misplaced global
# options block ("server block without any key…"). Only the composed environment renders the same
# file Caddy will actually run.
#
# …but it must NOT be `compose run caddy`: since #506 that service pins static IPs (10.60.0.100 /
# 10.61.0.100), and a one-off container built from it re-claims the very addresses the RUNNING Caddy
# holds — "failed to set up container networking: Address already in use" on every deploy after the
# first. `caddy-validate` is the same image + Caddyfile + environment (a YAML anchor keeps the env in
# lockstep) with no network and its own profile, so it can never collide with the live proxy.
log "Validating the Caddyfile"
compose --profile validate run --rm --no-deps caddy-validate

log "Pulling images from GHCR"
compose pull --quiet

# GHCR tags are MUTABLE. CD verified build provenance against DIGESTS on the runner; assert the bytes
# that just landed here are those same digests, before anything starts. A tag re-pointed between the
# runner's verify and this pull is caught right here instead of quietly running unattested code.
if [ -n "$EXPECTED_DIGESTS" ]; then
    log "Verifying pulled images against the digests CD attested"
    for expected in $EXPECTED_DIGESTS; do
        repo="${expected%@*}"
        ref="${repo}:${IMAGE_TAG}"
        actual="$(docker image inspect "$ref" --format '{{range .RepoDigests}}{{println .}}{{end}}' 2>/dev/null || true)"
        if ! printf '%s' "$actual" | grep -qxF "$expected"; then
            echo "::error::Digest mismatch for ${ref}" >&2
            echo "  expected (attested by CD): ${expected}" >&2
            echo "  actually pulled:           $(printf '%s' "$actual" | tr '\n' ' ')" >&2
            exit 1
        fi
        echo "  OK ${ref} -> ${expected#*@}"
    done
fi

# THE MIGRATION GATE. A one-off container (--no-deps: start nothing else), so the running apps are
# untouched while it works and a non-zero exit aborts the roll with the OLD release still serving.
# See the WARNING in the header for why `depends_on` cannot do this job.
log "Running migrations (gate — no app container is recreated unless this exits 0)"
compose run --rm --no-deps migrator

# Only now are the app containers recreated — onto a schema that is already migrated. `up -d` runs
# the migrator service once more as the apps' declared dependency; the second run is a no-op that
# exits 0 immediately (EF applies nothing when there is nothing pending).
log "Rolling containers"
compose up -d --remove-orphans

# compose recreates a container on an image or service-config change, but it does not look INSIDE a
# mounted file — so the Caddyfile this deploy just synced does not reach a Caddy that was already
# running. Reload unconditionally: it is hitless, and it is a no-op for a Caddy that `up -d` has just
# started with the new file. (Validated above, so this should not be the step that discovers a typo.)
log "Reloading Caddy (its bind-mounted Caddyfile is not hashed by compose)"
compose exec -T caddy caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile

# The roll is live and healthy — record it. Write-to-temp + rename, so a crash mid-write can never
# truncate the one file that holds every secret the stack runs on. mktemp creates the temp at 0600 —
# the mode the runbook mandates for .env — inside the stack dir, so the rename is atomic.
log "Pinning IMAGE_TAG=${IMAGE_TAG} in ${STACK_DIR}/.env"
env_tmp="$(mktemp "${STACK_DIR}/.env.XXXXXX")"
if grep -q '^IMAGE_TAG=' .env; then
    sed "s|^IMAGE_TAG=.*|IMAGE_TAG=${IMAGE_TAG}|" .env > "$env_tmp"
else
    cat .env > "$env_tmp"
    printf 'IMAGE_TAG=%s\n' "$IMAGE_TAG" >> "$env_tmp"
fi
mv "$env_tmp" .env

# Small VPS disk: 4 superseded images per deploy, each still carrying its sha-<short> tag (so never
# "dangling"). Sweep by AGE instead, keeping a fortnight of tags to roll back to. Anything a running
# container uses is never removed, whatever its age.
log "Pruning unused images older than ${PRUNE_UNUSED_OLDER_THAN}"
docker image prune --all --force --filter "until=${PRUNE_UNUSED_OLDER_THAN}" > /dev/null || true

log "Deployed ${IMAGE_TAG}"
compose ps
