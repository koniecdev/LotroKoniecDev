#!/usr/bin/env bash
# Test suite for the Hetzner rollout script (scripts/hetzner/deploy.sh — HETZ-04 / #490).
#
# deploy.sh is the one piece of CD that runs ON a production box, and its real inputs (a live Docker
# daemon, GHCR, Neon) are exactly what a test cannot have. So each case builds a throwaway stack dir
# (.env + a compose file) and puts a STUB `docker` first on PATH which records every invocation in
# order and fakes the outcomes the script branches on.
#
# The property that matters most here — and the one the first cut of this script got WRONG — is the
# ORDER: the migrator must run as its own gate BEFORE `up -d` recreates any app container. Compose's
# `depends_on: service_completed_successfully` gates the START of the apps, not their CREATION, so
# leaving the gate to compose destroys the old containers first and a failed migration takes the site
# DOWN instead of leaving the previous release serving (verified against Docker 29.6.1, #490 review).
# Several cases below assert exactly that ordering and that a failed gate never reaches `up -d`.
#
# CI runs this in the `guards` job, right next to the other bash gates.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
DEPLOY_SH="$SCRIPTS_DIR/hetzner/deploy.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

REPO='ghcr.io/koniecdev'
IMAGES='lotrokoniecdev-migrator lotrokoniecdev-auth-api lotrokoniecdev-tms-api lotrokoniecdev-frontend'
GOOD_DIGEST='sha256:1111111111111111111111111111111111111111111111111111111111111111'
OTHER_DIGEST='sha256:2222222222222222222222222222222222222222222222222222222222222222'

cases=0
CASE=""
LAST_OUTPUT=""
LAST_STATUS=0

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    printf '  --- docker calls ---\n'
    sed 's/^/    /' "$TMP_ROOT/calls.log" 2>/dev/null || true
    printf '  --- deploy.sh output ---\n%s\n' "$LAST_OUTPUT" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# A stack dir as scripts/hetzner/bootstrap.sh + the runbook leave one: compose file, Caddyfile,
# `.env` at 0600 already naming the currently-deployed tag.
new_stack() {
    local dir="$TMP_ROOT/stack-$1"
    mkdir -p "$dir/.docker/hetzner"
    # SC2016: ${IMAGE_TAG} is compose's own interpolation syntax — it must land in the file verbatim.
    # shellcheck disable=SC2016
    printf 'services:\n  migrator:\n    image: %s/lotrokoniecdev-migrator:${IMAGE_TAG}\n' "$REPO" \
        > "$dir/compose.hetzner.yaml"
    # SC2016: {$DOMAIN_APP} is Caddy's env placeholder — it must land in the file verbatim.
    # shellcheck disable=SC2016
    printf '{$DOMAIN_APP} {\n\treverse_proxy frontend:8080\n}\n' > "$dir/.docker/hetzner/Caddyfile"
    {
        echo 'COMPOSE_PROJECT_NAME=lotro-prod'
        echo 'IMAGE_TAG=sha-0000000'
        echo 'ConnectionStrings__AuthDatabase=Host=neon;Password=supersecret'
    } > "$dir/.env"
    chmod 600 "$dir/.env"
    printf '%s' "$dir"
}

# The stub `docker`. Behavior is steered by env vars the cases set:
#   STUB_CADDY_INVALID / STUB_PULL_FAIL / STUB_MIGRATE_FAIL / STUB_UP_FAIL — make that step exit 1
#   STUB_DIGEST — the digest `docker image inspect` reports for every image
# Every call is appended to $STUB_LOG *in order*, so a case can assert what ran, what did NOT, and
# crucially in which sequence.
install_stub_docker() {
    local bin="$TMP_ROOT/bin"
    mkdir -p "$bin"
    cat > "$bin/docker" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$STUB_LOG"

if [ "$1" = compose ]; then
    # docker compose -f <file> <verb> …
    verb=""
    seen_file=0
    for arg in "$@"; do
        case "$arg" in
            compose) continue ;;
            -f) seen_file=1; continue ;;
            *) if [ "$seen_file" = 1 ]; then seen_file=2; continue; fi
               verb="$arg"; break ;;
        esac
    done
    case "$verb" in
        pull) [ -z "${STUB_PULL_FAIL:-}" ] || { echo 'stub: pull failed' >&2; exit 1; } ;;
        run)
            # Two distinct `compose run` calls — tell them apart by what they run:
            #   `run --rm --no-deps caddy caddy validate …`  → the Caddyfile check
            #   `run --rm --no-deps migrator`                → the migration gate
            case "$*" in
                *validate*) [ -z "${STUB_CADDY_INVALID:-}" ] || { echo 'stub: Caddyfile invalid' >&2; exit 1; } ;;
                *migrator*) [ -z "${STUB_MIGRATE_FAIL:-}" ] || { echo 'stub: migration failed' >&2; exit 1; } ;;
            esac
            ;;
        up)   [ -z "${STUB_UP_FAIL:-}" ] || { echo 'stub: up failed' >&2; exit 1; } ;;
        ps)   echo 'NAME   STATUS' ;;
    esac
    exit 0
fi

case "$1 $2" in
    'image inspect')
        ref="$3"
        printf '%s@%s\n' "${ref%:*}" "${STUB_DIGEST}"
        ;;
    'image prune') ;;
esac
exit 0
STUB
    chmod +x "$bin/docker"
    PATH="$bin:$PATH"
    export PATH
}

run_deploy() {
    local dir="$1"
    shift
    STUB_LOG="$TMP_ROOT/calls.log"
    : > "$STUB_LOG"
    export STUB_LOG
    LAST_STATUS=0
    LAST_OUTPUT="$(env STACK_DIR="$dir" "$@" bash "$DEPLOY_SH" 2>&1)" || LAST_STATUS=$?
}

expected_digests() {
    local digest="$1" out="" image
    for image in $IMAGES; do
        out="${out}${out:+ }${REPO}/${image}@${digest}"
    done
    printf '%s' "$out"
}

env_tag() { sed -n 's/^IMAGE_TAG=//p' "$1/.env" | head -1; }
called() { grep -qF -- "$1" "$TMP_ROOT/calls.log"; }
# Line number of the first docker call containing $1 (0 = never called).
call_line() { grep -nF -- "$1" "$TMP_ROOT/calls.log" | head -1 | cut -d: -f1; }

install_stub_docker

# --- Input validation: a bad tag must die before it can reach compose or a remote shell -----------

CASE='no IMAGE_TAG'
stack="$(new_stack novar)"
run_deploy "$stack"
[ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 on a missing IMAGE_TAG' "got $LAST_STATUS"
pass 'a missing IMAGE_TAG is a usage error (exit 2)'

CASE='hostile IMAGE_TAG'
stack="$(new_stack hostile)"
run_deploy "$stack" IMAGE_TAG='sha-abc; rm -rf /'
[ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 on a tag carrying shell metacharacters' "got $LAST_STATUS"
[ "$(env_tag "$stack")" = 'sha-0000000' ] || fail '.env was rewritten with a rejected tag'
called 'pull' && fail 'a rejected tag still reached docker'
pass 'a tag outside [A-Za-z0-9._-] is rejected and nothing runs'

CASE='no .env'
stack="$(new_stack noenv)"
rm "$stack/.env"
run_deploy "$stack" IMAGE_TAG=sha-abc1234
[ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 when the box .env is missing' "got $LAST_STATUS"
pass 'a stack dir without .env is a usage error (exit 2)'

# --- Happy path + THE ordering property -----------------------------------------------------------

CASE='happy path'
stack="$(new_stack happy)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 EXPECTED_DIGESTS="$(expected_digests "$GOOD_DIGEST")" \
    STUB_DIGEST="$GOOD_DIGEST"
[ "$LAST_STATUS" -eq 0 ] || fail 'a clean roll should exit 0' "status $LAST_STATUS"
grep -q 'PREVIOUS_IMAGE_TAG=sha-0000000' <<<"$LAST_OUTPUT" || fail 'the previous tag was not printed for the rollback path'
[ "$(env_tag "$stack")" = 'sha-abc1234' ] || fail 'IMAGE_TAG was not pinned in .env' "$(cat "$stack/.env")"
grep -q 'supersecret' "$stack/.env" || fail 'rewriting .env dropped the other variables'
called 'caddy validate' || fail 'the Caddyfile was never validated'
called 'pull' || fail 'images were never pulled'
called 'run --rm --no-deps migrator' || fail 'the migration gate never ran'
called 'up -d --remove-orphans' || fail 'containers were never rolled'
called 'caddy reload' || fail 'Caddy was never reloaded — a changed Caddyfile would sit unapplied'
called 'image prune --all --force --filter until=' || fail 'the age-based image prune never ran'
pass 'validates, pulls, migrates, rolls, reloads Caddy, prunes, and pins the tag'

CASE='migration gate ordering'
# THE regression test for the #490 review finding: compose recreates app containers BEFORE it honours
# `depends_on: service_completed_successfully`, so the migrator MUST run as its own gate first.
gate_at="$(call_line 'run --rm --no-deps migrator')"
up_at="$(call_line 'up -d --remove-orphans')"
[ -n "$gate_at" ] && [ -n "$up_at" ] || fail 'expected both the migration gate and up -d to run'
[ "$gate_at" -lt "$up_at" ] \
    || fail 'the migrator must run BEFORE up -d recreates the apps' "gate at call #$gate_at, up -d at call #$up_at"
pass 'the migrator gate runs strictly BEFORE up -d recreates any app container'

CASE='.env mode'
# SC2012: the path is ours and has no exotic characters; `ls -l` is the one mode probe whose output
# is identical on the GNU (CI) and BSD (macOS dev) coreutils — `stat` needs a different flag on each.
# shellcheck disable=SC2012
[ "$(ls -l "$stack/.env" | cut -c1-10)" = '-rw-------' ] \
    || fail '.env must stay 0600 after the rewrite' "$(ls -l "$stack/.env")"
pass 'the rewritten .env is still 0600 (it holds every secret on the box)'

CASE='IMAGE_TAG absent from .env'
stack="$(new_stack notag)"
grep -v '^IMAGE_TAG=' "$stack/.env" > "$stack/.env.new" && mv "$stack/.env.new" "$stack/.env"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected a clean roll' "status $LAST_STATUS"
[ "$(env_tag "$stack")" = 'sha-abc1234' ] || fail 'IMAGE_TAG was not appended to a .env that lacked it'
pass 'a .env without IMAGE_TAG gets the line appended, not lost'

# --- A failed migration must be a NO-OP for the running site --------------------------------------

CASE='migration fails'
stack="$(new_stack migfail)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_MIGRATE_FAIL=1
[ "$LAST_STATUS" -ne 0 ] || fail 'a failed migration must fail the roll'
called 'up -d --remove-orphans' \
    && fail 'up -d ran after a FAILED migration — the old app containers would have been destroyed'
[ "$(env_tag "$stack")" = 'sha-0000000' ] \
    || fail '.env was moved to the new tag although the roll failed' "$(env_tag "$stack")"
grep -q 'PREVIOUS_IMAGE_TAG=sha-0000000' <<<"$LAST_OUTPUT" || fail 'CD was not told what to roll back to'
pass 'a failed migration never reaches up -d: the previous release keeps serving and .env still names it'

# --- The supply-chain assertion: attested digests are the ones that actually run -------------------

CASE='digest mismatch'
stack="$(new_stack mismatch)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 EXPECTED_DIGESTS="$(expected_digests "$GOOD_DIGEST")" \
    STUB_DIGEST="$OTHER_DIGEST"
[ "$LAST_STATUS" -ne 0 ] || fail 'a pulled image that is NOT the attested digest must fail the roll'
called 'run --rm --no-deps migrator' && fail 'the migration ran on an unattested image'
called 'up -d --remove-orphans' && fail 'the roll continued after a digest mismatch'
grep -q 'Digest mismatch' <<<"$LAST_OUTPUT" || fail 'the mismatch was not reported'
pass 'a tag re-pointed after CD attested it aborts the roll before migrating or starting anything'

CASE='no digests supplied'
stack="$(new_stack nodigest)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$OTHER_DIGEST"
[ "$LAST_STATUS" -eq 0 ] || fail 'a hand-run deploy (no EXPECTED_DIGESTS) should skip the assertion' "status $LAST_STATUS"
called 'up -d --remove-orphans' || fail 'a hand-run deploy should still roll'
pass 'a hand-run deploy without EXPECTED_DIGESTS skips the assertion and still rolls'

# --- Every other abort must also leave the running site alone --------------------------------------

CASE='broken Caddyfile'
stack="$(new_stack caddybad)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_CADDY_INVALID=1
[ "$LAST_STATUS" -ne 0 ] || fail 'an invalid Caddyfile must fail the roll'
called 'pull' && fail 'a broken Caddyfile should abort before anything is pulled'
called 'up -d --remove-orphans' && fail 'containers were rolled with a broken Caddyfile'
[ "$(env_tag "$stack")" = 'sha-0000000' ] || fail '.env moved although the roll failed'
pass 'an invalid Caddyfile aborts up front — the running Caddy keeps serving its current config'

CASE='pull fails'
stack="$(new_stack pullfail)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_PULL_FAIL=1
[ "$LAST_STATUS" -ne 0 ] || fail 'a failed pull must fail the script'
called 'run --rm --no-deps migrator' && fail 'the migration ran although the pull failed'
called 'up -d --remove-orphans' && fail 'containers were rolled although the pull failed'
[ "$(env_tag "$stack")" = 'sha-0000000' ] || fail '.env moved although the roll failed'
pass 'a failed pull aborts before any container is touched and .env still names the live tag'

CASE='up fails'
stack="$(new_stack upfail)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_UP_FAIL=1
[ "$LAST_STATUS" -ne 0 ] || fail 'a failed up -d must fail the script'
[ "$(env_tag "$stack")" = 'sha-0000000' ] \
    || fail '.env must still name the previous tag when the roll failed' "$(env_tag "$stack")"
grep -q 'PREVIOUS_IMAGE_TAG=sha-0000000' <<<"$LAST_OUTPUT" || fail 'CD was not told what to roll back to'
grep -q 'Deploy FAILED' <<<"$LAST_OUTPUT" || fail 'the failure banner (with container logs) was not printed'
called 'logs' || fail 'container logs were not dumped on failure'
pass 'a failed up -d exits non-zero, dumps logs, leaves .env on the live tag, and names the rollback target'

printf '\nAll %d hetzner deploy.sh case(s) passed.\n' "$cases"
