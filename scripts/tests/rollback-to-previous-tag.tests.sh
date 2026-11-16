#!/usr/bin/env bash
# Test suite for scripts/ci/rollback-to-previous-tag.sh (#1336), deploy.yml's automatic rollback.
#
# It runs only during an incident, so nothing else in CI ever exercises it. Each case runs the real
# script and the real resolve-verified-digests.sh behind it, with STUB `docker`, `gh` and `ssh` first
# on PATH; each stub logs its argv. The properties that matter:
#
#   * A verified tag: one ssh that restores the config, then runs deploy.sh with the tag AND the
#     checked digests. The tag is checked against main whatever the run's ref is, and a failed lookup
#     is tried again before the images are given up.
#   * A tag that is not verified (refused, lookup failed, the check misconfigured, or hostile): one
#     ssh that only restores the config, never deploy.sh, never the tag; "Not rolled back" with the
#     right reason in the summary; exit 1.
#   * Every outcome writes its summary, a failed ssh included.
#
# CI runs this in the `guards` job, next to the other bash gates.

set -euo pipefail

SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
SCRIPT="$SCRIPTS_DIR/ci/rollback-to-previous-tag.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

cases=0
CASE=""
LAST_STATUS=0
OUT="$TMP_ROOT/output"
CALLS="$TMP_ROOT/calls"
SUMMARY="$TMP_ROOT/summary"
REPO_PREFIX='ghcr.io/koniecdev'
DIGEST="sha256:$(printf '%064d' 7)"
# The remote command as the box receives it: $f is the box's variable, so it stays unexpanded here.
# shellcheck disable=SC2016
RESTORE='cd /opt/lotro || exit 1; for f in compose.hetzner.yaml .docker/hetzner/Caddyfile deploy.sh; do [ -f ".previous/$f" ] && cp -p ".previous/$f" "$f" || true; done'

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    printf '  --- output ---\n%s\n  --- summary ---\n%s\n  --- calls ---\n%s\n' \
        "$(cat "$OUT")" "$(cat "$SUMMARY" 2>/dev/null)" "$(cat "$CALLS" 2>/dev/null)" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# --- Stubs --------------------------------------------------------------------------------------
# STUB_DOCKER_FAIL — the digest lookup fails
# STUB_GH_REFUSE   — the provenance check refuses every image
# STUB_GH_FAIL_FIRST — the first N gh calls fail (a short outage), the rest pass
# STUB_SSH_RC      — the exit code of ssh (default 0)
BIN="$TMP_ROOT/bin"
mkdir -p "$BIN"
cat > "$BIN/docker" <<'STUB'
#!/usr/bin/env bash
printf 'docker %s\n' "$*" >> "$STUB_CALLS"
[ -z "${STUB_DOCKER_FAIL:-}" ] || { echo 'stub: registry unreachable' >&2; exit 1; }
printf '%s\n' "$STUB_DIGEST"
STUB
cat > "$BIN/gh" <<'STUB'
#!/usr/bin/env bash
printf 'gh %s\n' "$*" >> "$STUB_CALLS"
[ -z "${STUB_GH_REFUSE:-}" ] || { echo 'stub: verification failed' >&2; exit 1; }
if [ -n "${STUB_GH_FAIL_FIRST:-}" ]; then
    count=$(( $(cat "$STUB_GH_COUNTER" 2>/dev/null || echo 0) + 1 ))
    echo "$count" > "$STUB_GH_COUNTER"
    [ "$count" -gt "$STUB_GH_FAIL_FIRST" ] || { echo 'stub: attestation service unavailable' >&2; exit 1; }
fi
echo 'stub: verified'
STUB
cat > "$BIN/ssh" <<'STUB'
#!/usr/bin/env bash
printf 'ssh %s\n' "$*" >> "$STUB_CALLS"
exit "${STUB_SSH_RC:-0}"
STUB
chmod +x "$BIN/docker" "$BIN/gh" "$BIN/ssh"

run_rollback() {
    : > "$CALLS"
    : > "$SUMMARY"
    rm -f "$TMP_ROOT/gh_counter"
    LAST_STATUS=0
    env PATH="$BIN:$PATH" \
        STUB_CALLS="$CALLS" STUB_DIGEST="$DIGEST" STUB_GH_COUNTER="$TMP_ROOT/gh_counter" \
        ROLLBACK_VERIFY_DELAY=0 \
        GITHUB_STEP_SUMMARY="$SUMMARY" \
        REGISTRY='ghcr.io' IMAGE_NAMESPACE='koniecdev' \
        GITHUB_REPOSITORY='koniecdev/LotroKoniecDev' GITHUB_REF='refs/heads/main' \
        STACK_DIR='/opt/lotro' ENVIRONMENT='staging' HETZNER_HOST='203.0.113.9' \
        PREVIOUS_IMAGE_TAG='sha-1a2b3c4' \
        "$@" \
        "$SCRIPT" > "$OUT" 2>&1 || LAST_STATUS=$?
}

ssh_calls() {
    grep -c '^ssh ' "$CALLS" || true
}

expect_restore_only() {
    [ "$(ssh_calls)" -eq 1 ] || fail 'expected exactly one ssh call'
    grep -qxF "ssh box set -e; ${RESTORE}" "$CALLS" || fail 'the ssh call must restore the config and do nothing else'
    if grep '^ssh ' "$CALLS" | grep -q 'IMAGE_TAG\|bash /opt/lotro/deploy.sh'; then fail 'an unverified tag must never reach deploy.sh'; fi
    grep -q '### ⚠️ Not rolled back' "$SUMMARY" || fail 'the summary must say "Not rolled back"'
}

# --- A verified tag -------------------------------------------------------------------------------

CASE='verified'
run_rollback GITHUB_REF='refs/heads/1336-proof-branch'
[ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
digests="${REPO_PREFIX}/lotrokoniecdev-migrator@${DIGEST} ${REPO_PREFIX}/lotrokoniecdev-auth-api@${DIGEST} ${REPO_PREFIX}/lotrokoniecdev-tms-api@${DIGEST} ${REPO_PREFIX}/lotrokoniecdev-frontend@${DIGEST}"
[ "$(ssh_calls)" -eq 1 ] || fail 'expected exactly one ssh call'
grep -qxF "ssh box set -e; ${RESTORE}; IMAGE_TAG='sha-1a2b3c4' EXPECTED_DIGESTS='${digests}' bash /opt/lotro/deploy.sh" "$CALLS" \
    || fail 'the ssh call must restore the config, then run deploy.sh with the tag and the four checked digests'
[ "$(grep -c '^gh attestation verify' "$CALLS")" -eq 4 ] || fail 'each image must pass the provenance check first'
[ "$(grep '^gh attestation verify' "$CALLS" | grep -c -- '--source-ref refs/heads/main$')" -eq 4 ] \
    || fail 'the previous tag must be checked against main, not against the run ref'
grep -q '### ⏮️ Rolled back' "$SUMMARY" || fail 'the summary must say "Rolled back"'
pass 'checks the tag against main even on a branch run, restores the config, rolls back with the checked digests'

CASE='short outage'
run_rollback STUB_GH_FAIL_FIRST=1
[ "$LAST_STATUS" -eq 0 ] || fail "expected the second attempt to roll back, got $LAST_STATUS"
grep -q 'trying again' "$OUT" || fail 'a failed attempt must say it tries again'
grep -q '### ⏮️ Rolled back' "$SUMMARY" || fail 'the summary must say "Rolled back"'
pass 'a provenance check that fails once is tried again, and the rollback goes on'

CASE='verified, ssh fails'
run_rollback STUB_SSH_RC=3
[ "$LAST_STATUS" -eq 3 ] || fail "expected the ssh exit code 3, got $LAST_STATUS"
grep -q '### ⚠️ Rollback failed' "$SUMMARY" || fail 'the summary must say "Rollback failed"'
grep -q 'exited 3' "$SUMMARY" || fail 'the summary must give the exit code'
pass 'a failed rollback ssh exits with its code and still writes the summary'

# --- A tag that is not verified -----------------------------------------------------------------

CASE='provenance refused'
run_rollback STUB_GH_REFUSE=1
[ "$LAST_STATUS" -eq 1 ] || fail "expected exit 1, got $LAST_STATUS"
expect_restore_only
grep -q 'was restored from' "$SUMMARY" || fail 'the summary must say the config was restored'
grep -q '::error::Not rolled back to sha-1a2b3c4: either its provenance check refused it' "$OUT" || fail 'the log must carry the ::error:: line'
[ "$(grep '^gh attestation verify' "$CALLS" | grep -c 'lotrokoniecdev-migrator@')" -eq 3 ] || fail 'a refusal must be tried exactly 3 times'
pass 'a refused tag is tried 3 times, then restores only the config, says "Not rolled back" and exits 1'

CASE='lookup failed'
run_rollback STUB_DOCKER_FAIL=1
[ "$LAST_STATUS" -eq 1 ] || fail "expected exit 1, got $LAST_STATUS"
expect_restore_only
grep -q 'digests could not be looked up' "$SUMMARY" || fail 'the summary must allow for a failed lookup, not only a refusal'
pass 'a failed digest lookup is treated like a refusal, and the summary does not blame provenance alone'

CASE='refused, restore fails'
run_rollback STUB_GH_REFUSE=1 STUB_SSH_RC=255
[ "$LAST_STATUS" -eq 1 ] || fail "expected exit 1, got $LAST_STATUS"
expect_restore_only
grep -q 'also failed (exit 255)' "$SUMMARY" || fail 'the summary must say the config restore failed too'
pass 'a refused tag whose config restore fails still writes the summary and exits 1'

CASE='check misconfigured'
run_rollback REGISTRY=
[ "$LAST_STATUS" -eq 1 ] || fail "expected exit 1, got $LAST_STATUS"
expect_restore_only
if grep -q '^docker \|^gh ' "$CALLS"; then fail 'a misconfigured check must not reach the registry'; fi
grep -q 'could not run (exit 2' "$SUMMARY" || fail 'the summary must name a misconfigured check, not blame provenance'
if grep -q 'trying again' "$OUT"; then fail 'a usage error must not be tried again'; fi
pass 'a misconfigured check is reported as such, is not retried, and only the config is restored'

CASE='hostile tag'
run_rollback PREVIOUS_IMAGE_TAG="sha-1;id"
[ "$LAST_STATUS" -eq 1 ] || fail "expected exit 1, got $LAST_STATUS"
expect_restore_only
grep -q 'not a valid image tag' "$SUMMARY" || fail 'the summary must say the tag is not valid'
if grep -q '^docker \|^gh ' "$CALLS"; then fail 'a hostile tag must stop before any registry call'; fi
if grep '^ssh ' "$CALLS" | grep -q ';id'; then fail 'a hostile tag must never reach the remote command'; fi
pass 'a hostile tag is never looked up and never reaches ssh; only the config is restored'

# --- Usage errors ---------------------------------------------------------------------------------

for name in PREVIOUS_IMAGE_TAG ENVIRONMENT STACK_DIR; do
    CASE="no ${name}"
    run_rollback "${name}="
    [ "$LAST_STATUS" -eq 2 ] || fail "expected exit 2, got $LAST_STATUS"
    [ ! -s "$CALLS" ] || fail 'a usage error must stop before any call'
    pass "an empty ${name} is a usage error (exit 2) before any call"
done

printf '\nAll %d rollback-to-previous-tag case(s) passed.\n' "$cases"
