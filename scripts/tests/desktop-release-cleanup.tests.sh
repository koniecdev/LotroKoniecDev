#!/usr/bin/env bash
# Test suite for scripts/ci/desktop-release-cleanup.sh (spec 0016, #45), the clean-up after a desktop
# release job that stopped after it made its tag.
#
# The script runs only when a release fails or is cancelled, and two of its endings delete things on
# GitHub. So each case runs it with a STUB `gh` first on PATH, and checks which calls it made, in
# which order. CI runs this in the `guards` job, next to the other bash gates.

set -euo pipefail
export LC_ALL=C

SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
SCRIPT="$SCRIPTS_DIR/ci/desktop-release-cleanup.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

cases=0
CASE=""
LAST_STATUS=0
OUT="$TMP_ROOT/stdout"
ERR="$TMP_ROOT/stderr"
CALLS="$TMP_ROOT/calls"

VIEW_CALL='gh release view desktop-v0.2.0 --json isDraft --jq .isDraft'
RELEASE_DELETE_CALL='gh release delete desktop-v0.2.0 --yes'
TAG_DELETE_CALL='gh api -X DELETE repos/koniecdev/LotroKoniecDev/git/refs/tags/desktop-v0.2.0'

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

# --- Stub ---------------------------------------------------------------------------------------
# STUB_VIEW_OUT          — what `gh release view` prints (default: true, a draft)
# STUB_VIEW_ERR          — `gh release view` prints this on stderr and exits 1
# STUB_DELETE_FAIL       — `gh release delete` exits 1
# STUB_REF_DELETE_FAIL   — the tag delete exits 1
BIN="$TMP_ROOT/bin"
mkdir -p "$BIN"
cat > "$BIN/gh" <<'STUB'
#!/usr/bin/env bash
printf 'gh %s\n' "$*" >> "$STUB_CALLS"
case "$1 $2" in
    'release view')
        if [ -n "${STUB_VIEW_ERR:-}" ]; then
            printf '%s\n' "$STUB_VIEW_ERR" >&2
            exit 1
        fi
        printf '%s\n' "${STUB_VIEW_OUT-true}"
        ;;
    'release delete')
        [ -z "${STUB_DELETE_FAIL:-}" ] || { echo 'stub: release delete failed' >&2; exit 1; }
        ;;
    'api -X')
        [ -z "${STUB_REF_DELETE_FAIL:-}" ] || { echo 'stub: tag delete failed' >&2; exit 1; }
        ;;
    *)
        echo "stub gh: unexpected call '$*'" >&2
        exit 1
        ;;
esac
STUB
chmod +x "$BIN/gh"

# Runs the clean-up for version 0.2.0; `VAR=value` arguments win.
run_cleanup() {
    : > "$CALLS"
    LAST_STATUS=0
    env PATH="$BIN:$PATH" \
        STUB_CALLS="$CALLS" \
        GH_REPO='koniecdev/LotroKoniecDev' \
        VERSION='0.2.0' \
        "$@" \
        bash "$SCRIPT" > "$OUT" 2> "$ERR" || LAST_STATUS=$?
}

expect_status() {
    [ "$LAST_STATUS" -eq "$1" ] || fail "expected exit $1, got $LAST_STATUS"
}

expect_calls() {
    local expected
    expected="$(printf '%s\n' "$@")"
    [ "$(cat "$CALLS")" = "$expected" ] || fail "expected the calls:
$expected"
}

expect_warning() {
    grep -qF -- "::warning::$1" "$OUT" || fail "expected the warning: $1"
}

# --- the tag goes -------------------------------------------------------------------------------

CASE='draft'
run_cleanup STUB_VIEW_OUT=true
expect_status 0
expect_calls "$VIEW_CALL" "$RELEASE_DELETE_CALL" "$TAG_DELETE_CALL"
grep -qF -- 'Removed the tag desktop-v0.2.0.' "$OUT" || fail 'expected the removal line'
pass 'a draft release is deleted first, then its tag'

CASE='no release'
run_cleanup STUB_VIEW_ERR='release not found'
expect_status 0
expect_calls "$VIEW_CALL" "$TAG_DELETE_CALL"
pass "gh's \"release not found\" deletes only the tag"

# --- the tag stays ------------------------------------------------------------------------------

CASE='published'
run_cleanup STUB_VIEW_OUT=false
expect_status 0
expect_calls "$VIEW_CALL"
expect_warning 'desktop-v0.2.0 was published before the job stopped; the release and its tag stay.'
pass 'a public release and its tag stay'

for failure in 'HTTP 502: Bad Gateway (https://api.github.com/repos/koniecdev/LotroKoniecDev/releases)' \
               'error connecting to api.github.com' \
               'HTTP 401: Bad credentials' \
               'HTTP 404: Not Found (https://api.github.com/repos/koniecdev/LotroKoniecDev/releases?per_page=100&page=1)' \
               'gh: command not found' \
               ' '; do
    CASE="view failed: '${failure}'"
    run_cleanup STUB_VIEW_ERR="$failure"
    expect_status 0
    expect_calls "$VIEW_CALL"
    expect_warning 'Could not check whether desktop-v0.2.0 has a release, so its tag stays.'
    pass 'any other failure to ask keeps the tag'
done

for answer in '' 'null' 'maybe' 'True' 'false true'; do
    CASE="answer '${answer}'"
    run_cleanup STUB_VIEW_OUT="$answer"
    expect_status 0
    expect_calls "$VIEW_CALL"
    expect_warning "Unexpected answer '${answer}' about desktop-v0.2.0, so its tag stays."
    pass 'an answer that is neither true nor false keeps the tag'
done

# --- a delete fails -----------------------------------------------------------------------------

CASE='draft delete fails'
run_cleanup STUB_VIEW_OUT=true STUB_DELETE_FAIL=1
[ "$LAST_STATUS" -ne 0 ] || fail 'expected a failing exit'
expect_calls "$VIEW_CALL" "$RELEASE_DELETE_CALL"
pass 'when the draft cannot be deleted, the tag stays and the step fails'

CASE='tag delete fails'
run_cleanup STUB_VIEW_OUT=true STUB_REF_DELETE_FAIL=1
[ "$LAST_STATUS" -ne 0 ] || fail 'expected a failing exit'
expect_calls "$VIEW_CALL" "$RELEASE_DELETE_CALL" "$TAG_DELETE_CALL"
grep -qF -- 'Removed the tag' "$OUT" && fail 'a failed tag delete must not report a removal'
pass 'a tag that cannot be deleted fails the step'

# --- usage --------------------------------------------------------------------------------------

for name in VERSION GH_REPO; do
    CASE="no ${name}"
    run_cleanup "${name}="
    expect_status 2
    [ ! -s "$CALLS" ] || fail 'a usage error must not call gh'
    grep -qF -- "::error::desktop-release-cleanup.sh: ${name} is not set." "$ERR" || fail 'expected the usage reason'
    pass "an empty ${name} is a usage error (exit 2), before any call"
done

printf '\nAll %d desktop-release-cleanup case(s) passed.\n' "$cases"
