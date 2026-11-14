#!/usr/bin/env bash
# Test suite for scripts/ci/resolve-verified-digests.sh (#1336), the provenance check that the CD
# rollout, its automatic rollback and the runbook's hand rollback all call.
#
# GHCR and the attestation service exist nowhere in CI, so each case runs the script with STUB
# `docker` and `gh` binaries first on PATH, steered by env vars. Both stubs log their argv. The
# properties that matter:
#
#   * Only the exact check passes: `gh` gets the DIGEST, never the tag, and always `--repo`,
#     `--signer-workflow <repo>/.github/workflows/cd.yml` and `--source-ref <the run's ref>`.
#   * Fail closed with an empty stdout: a caller appends stdout to $GITHUB_ENV, so a refusal must
#     never leave a partial EXPECTED_DIGESTS behind.
#   * A missing setting or a bad tag stops the script before any call, because an empty flag would
#     make the check weaker, not stricter.
#
# CI runs this in the `guards` job, next to the other bash gates.

set -euo pipefail

SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
SCRIPT="$SCRIPTS_DIR/ci/resolve-verified-digests.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

cases=0
CASE=""
LAST_STATUS=0
OUT="$TMP_ROOT/stdout"
ERR="$TMP_ROOT/stderr"
CALLS="$TMP_ROOT/calls"

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    printf '  --- stdout ---\n%s\n  --- stderr ---\n%s\n  --- calls ---\n%s\n' \
        "$(cat "$OUT")" "$(cat "$ERR")" "$(cat "$CALLS" 2>/dev/null)" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

digest_of() {
    printf 'sha256:%064d' "$1"
}
D_MIGRATOR="$(digest_of 1)"
D_AUTH="$(digest_of 2)"
D_TMS="$(digest_of 3)"
D_FRONTEND="$(digest_of 4)"

# --- Stubs ------------------------------------------------------------------------------------
# STUB_DOCKER_FAIL   — `docker buildx imagetools inspect` exits 1
# STUB_DIGEST_<NAME> — what docker prints for that image (default: its D_* digest above)
# STUB_GH_REFUSE     — the image name whose attestation check `gh` refuses
BIN="$TMP_ROOT/bin"
mkdir -p "$BIN"
cat > "$BIN/docker" <<'STUB'
#!/usr/bin/env bash
printf 'docker %s\n' "$*" >> "$STUB_CALLS"
[ -z "${STUB_DOCKER_FAIL:-}" ] || { echo 'stub: registry unreachable' >&2; exit 1; }
ref="$4"
case "$ref" in
    */lotrokoniecdev-migrator:*) printf '%s\n' "${STUB_DIGEST_MIGRATOR-$D_MIGRATOR}" ;;
    */lotrokoniecdev-auth-api:*) printf '%s\n' "${STUB_DIGEST_AUTH-$D_AUTH}" ;;
    */lotrokoniecdev-tms-api:*) printf '%s\n' "${STUB_DIGEST_TMS-$D_TMS}" ;;
    */lotrokoniecdev-frontend:*) printf '%s\n' "${STUB_DIGEST_FRONTEND-$D_FRONTEND}" ;;
    *) echo "stub docker: unexpected ref '$ref'" >&2; exit 1 ;;
esac
STUB
cat > "$BIN/gh" <<'STUB'
#!/usr/bin/env bash
printf 'gh %s\n' "$*" >> "$STUB_CALLS"
if [ -n "${STUB_GH_REFUSE:-}" ] && [[ "$3" == *"/${STUB_GH_REFUSE}@"* ]]; then
    echo 'stub: verification failed' >&2
    exit 1
fi
echo 'stub: verified'
STUB
chmod +x "$BIN/docker" "$BIN/gh"

REPO_PREFIX='ghcr.io/koniecdev'

# Runs the script with the default settings; extra `VAR=value` arguments go before them in `env`
# and win, and `-u VAR` arguments unset one.
run_script() {
    local tag="$1"
    shift
    : > "$CALLS"
    LAST_STATUS=0
    env PATH="$BIN:$PATH" \
        STUB_CALLS="$CALLS" \
        D_MIGRATOR="$D_MIGRATOR" D_AUTH="$D_AUTH" D_TMS="$D_TMS" D_FRONTEND="$D_FRONTEND" \
        REGISTRY='ghcr.io' \
        IMAGE_NAMESPACE='koniecdev' \
        GITHUB_REPOSITORY='koniecdev/LotroKoniecDev' \
        GITHUB_REF='refs/heads/main' \
        "$@" \
        "$SCRIPT" "$tag" > "$OUT" 2> "$ERR" || LAST_STATUS=$?
}

# --- The happy path ---------------------------------------------------------------------------

CASE='all four verified'
run_script sha-1a2b3c4
[ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
expected="EXPECTED_DIGESTS=${REPO_PREFIX}/lotrokoniecdev-migrator@${D_MIGRATOR} ${REPO_PREFIX}/lotrokoniecdev-auth-api@${D_AUTH} ${REPO_PREFIX}/lotrokoniecdev-tms-api@${D_TMS} ${REPO_PREFIX}/lotrokoniecdev-frontend@${D_FRONTEND}"
[ "$(cat "$OUT")" = "$expected" ] || fail 'stdout must be exactly one EXPECTED_DIGESTS line with the four digests in order'
[ "$(wc -l < "$OUT")" -eq 1 ] || fail 'stdout must hold one line only'
pass 'prints one EXPECTED_DIGESTS line with the four repo@digest refs, in a fixed order'

CASE='the exact check'
run_script sha-1a2b3c4 GITHUB_REF='refs/heads/1336-some-branch'
[ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
[ "$(grep -c '^gh ' "$CALLS")" -eq 4 ] || fail 'expected one gh call per image'
for pair in "migrator:$D_MIGRATOR" "auth-api:$D_AUTH" "tms-api:$D_TMS" "frontend:$D_FRONTEND"; do
    image="${pair%%:*}"
    digest="${pair#*:}"
    line="gh attestation verify oci://${REPO_PREFIX}/lotrokoniecdev-${image}@${digest} --repo koniecdev/LotroKoniecDev --signer-workflow koniecdev/LotroKoniecDev/.github/workflows/cd.yml --source-ref refs/heads/1336-some-branch"
    grep -qxF "$line" "$CALLS" || fail "missing or different gh call for ${image}"
done
if grep '^gh ' "$CALLS" | grep -q ':sha-1a2b3c4'; then fail 'gh must verify the digest, never the tag'; fi
pass 'gh checks each digest with --repo, the cd.yml signer and the run ref, never the tag'

# --- Refusals fail closed with an empty stdout --------------------------------------------------

CASE='attestation refused'
run_script sha-1a2b3c4 STUB_GH_REFUSE=lotrokoniecdev-tms-api
[ "$LAST_STATUS" -eq 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ ! -s "$OUT" ] || fail 'a refusal must leave stdout empty'
grep -q '::error::.*lotrokoniecdev-tms-api:sha-1a2b3c4 was not built and attested' "$ERR" || fail 'the refusal must name the image'
if grep -q 'lotrokoniecdev-frontend' "$CALLS"; then fail 'the script must stop at the first refused image'; fi
pass 'a refused attestation exits 1, names the image, prints nothing to stdout and stops there'

for bad in '' 'latest' 'sha256:abc' "sha256:$(printf '%064d' 3);x" "SHA256:$(printf '%064d' 3)" "sha256:$(printf '%063d' 3)G"; do
    CASE="bad digest '${bad}'"
    run_script sha-1a2b3c4 STUB_DIGEST_TMS="$bad"
    [ "$LAST_STATUS" -eq 1 ] || fail "expected exit 1, got $LAST_STATUS"
    [ ! -s "$OUT" ] || fail 'a bad digest must leave stdout empty'
    if grep '^gh ' "$CALLS" | grep -q 'lotrokoniecdev-tms-api'; then fail 'a bad digest must never reach gh'; fi
    pass 'a digest that is not sha256:<64 hex> is refused before gh'
done

CASE='registry unreachable'
run_script sha-1a2b3c4 STUB_DOCKER_FAIL=1
[ "$LAST_STATUS" -eq 1 ] || fail "expected exit 1, got $LAST_STATUS"
[ ! -s "$OUT" ] || fail 'a failed lookup must leave stdout empty'
if grep -q '^gh ' "$CALLS"; then fail 'no gh call may follow a failed lookup'; fi
pass 'a failed digest lookup exits 1 with an empty stdout'

# --- Usage errors stop before any call ----------------------------------------------------------

for name in REGISTRY IMAGE_NAMESPACE GITHUB_REPOSITORY GITHUB_REF; do
    CASE="no ${name}"
    run_script sha-1a2b3c4 "${name}="
    [ "$LAST_STATUS" -eq 2 ] || fail "expected exit 2, got $LAST_STATUS"
    [ ! -s "$OUT" ] || fail 'a usage error must leave stdout empty'
    [ ! -s "$CALLS" ] || fail 'a usage error must stop before any docker or gh call'
    pass "an empty ${name} is a usage error (exit 2) before any call"
done

for bad in '' 'a;b' 'sha-1 x' "sha-\$(id)" 'sha/1'; do
    CASE="bad tag '${bad}'"
    run_script "$bad"
    [ "$LAST_STATUS" -eq 2 ] || fail "expected exit 2, got $LAST_STATUS"
    [ ! -s "$OUT" ] || fail 'a bad tag must leave stdout empty'
    [ ! -s "$CALLS" ] || fail 'a bad tag must stop before any docker or gh call'
    pass 'a tag outside [A-Za-z0-9._-] is a usage error (exit 2) before any call'
done

printf '\nAll %d resolve-verified-digests case(s) passed.\n' "$cases"
