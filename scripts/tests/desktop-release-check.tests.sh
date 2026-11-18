#!/usr/bin/env bash
# Test suite for scripts/ci/desktop-release-check.sh (spec 0016, #45), the checks a desktop release
# passes before anything is built.
#
# The GitHub API exists nowhere in CI, so each case runs the check with a STUB `gh` first on PATH that
# prints the tag list a case sets. Every refusal gets its own case, because three of them never run on
# a normal release: they run on the day someone makes a mistake.
#
# CI runs this in the `guards` job, next to the other bash gates.

set -euo pipefail
export LC_ALL=C

SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
SCRIPT="$SCRIPTS_DIR/ci/desktop-release-check.sh"
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

# --- Stub ---------------------------------------------------------------------------------------
# STUB_REFS     — the refs the matching-refs call prints, one per line (default: none)
# STUB_GH_FAIL  — every gh call exits 1
BIN="$TMP_ROOT/bin"
mkdir -p "$BIN"
cat > "$BIN/gh" <<'STUB'
#!/usr/bin/env bash
printf 'gh %s\n' "$*" >> "$STUB_CALLS"
[ -z "${STUB_GH_FAIL:-}" ] || { echo 'stub: gh api failed' >&2; exit 1; }
case "$2" in
    repos/koniecdev/LotroKoniecDev/git/matching-refs/tags/desktop-v) printf '%s' "${STUB_REFS:-}" ;;
    *) echo "stub gh: unexpected call '$*'" >&2; exit 1 ;;
esac
STUB
chmod +x "$BIN/gh"

# Runs the check for version 0.2.0 from main with two earlier desktop tags; `VAR=value` arguments win.
run_check() {
    : > "$CALLS"
    LAST_STATUS=0
    env PATH="$BIN:$PATH" \
        STUB_CALLS="$CALLS" \
        GITHUB_REPOSITORY='koniecdev/LotroKoniecDev' \
        GITHUB_REF='refs/heads/main' \
        VERSION='0.2.0' \
        STUB_REFS=$'refs/tags/desktop-v0.1.0\nrefs/tags/desktop-v0.1.1\n' \
        "$@" \
        bash "$SCRIPT" > "$OUT" 2> "$ERR" || LAST_STATUS=$?
}

expect_accepted() {
    [ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
    grep -qF -- "$1" "$OUT" || fail "expected stdout to contain: $1"
}

expect_refused() {
    [ "$LAST_STATUS" -eq 1 ] || fail "expected exit 1, got $LAST_STATUS"
    grep -qF -- "::error::$1" "$ERR" || fail "expected the reason: $1"
}

# --- accepted -----------------------------------------------------------------------------------

CASE='next version'
run_check
expect_accepted 'Releasing desktop-v0.2.0 (highest so far: 0.1.1).'
pass 'a version above the highest tag goes on'

CASE='first release'
run_check VERSION=0.1.0 STUB_REFS=
expect_accepted 'Releasing desktop-v0.1.0 (highest so far: none).'
pass 'with no desktop tag yet, any valid version goes on'

CASE='version order'
run_check VERSION=0.10.0 STUB_REFS=$'refs/tags/desktop-v0.9.0\nrefs/tags/desktop-v0.2.0\n'
expect_accepted 'Releasing desktop-v0.10.0 (highest so far: 0.9.0).'
pass '0.10.0 is above 0.9.0, as numbers, not as text'

CASE='other tags'
run_check VERSION=0.2.0 STUB_REFS=$'refs/tags/desktop-v0.1.0\nrefs/tags/desktop-v9.0.0-rc1\nrefs/tags/desktop-v09.0.0\nrefs/tags/desktop-vbroken\n'
expect_accepted 'Releasing desktop-v0.2.0 (highest so far: 0.1.0).'
pass 'tags off the grammar are not counted as the highest'

CASE='largest parts'
run_check VERSION=999999999.999999999.999999999
expect_accepted 'Releasing desktop-v999999999.999999999.999999999'
pass 'nine digits a part are allowed'

# --- refused ------------------------------------------------------------------------------------

for ref in refs/heads/feature refs/tags/main refs/heads/main-2 main refs/pull/1/merge; do
    CASE="ref ${ref}"
    run_check GITHUB_REF="$ref"
    expect_refused "A desktop release runs only from main, not from ${ref}."
    [ ! -s "$CALLS" ] || fail 'a refused ref must not call the API'
    pass 'only refs/heads/main may release'
done

for version in '' ' ' '1' '1.2' '1.2.3.4' '01.2.3' '1.02.3' '1.2.03' '1.2.3-rc1' 'v1.2.3' 'desktop-v1.2.3' \
               ' 1.2.3' '1.2.3 ' $'1.2.3\n' '1..3' '-1.2.3' '1.2.x' '1000000000.0.0'; do
    CASE="version '${version}'"
    run_check VERSION="$version"
    expect_refused 'The version must be three plain numbers such as 0.1.0, without a prefix.'
    [ ! -s "$CALLS" ] || fail 'a refused version must not call the API'
    pass 'a version off the grammar is refused before any API call'
done

CASE='no version input'
run_check VERSION=
expect_refused 'The version must be three plain numbers'
pass 'an empty version is refused'

CASE='developer build'
run_check VERSION=0.0.0
expect_refused '0.0.0 is the developer build number and is never released.'
[ ! -s "$CALLS" ] || fail '0.0.0 must not call the API'
pass '0.0.0 is never released, before any API call'

CASE='tag exists'
run_check VERSION=0.1.1
expect_refused 'The tag desktop-v0.1.1 exists already.'
pass 'an existing tag is refused'

CASE='only tag exists'
run_check VERSION=0.3.0 STUB_REFS=$'refs/tags/desktop-v0.3.0\n'
expect_refused 'The tag desktop-v0.3.0 exists already.'
pass 'an existing tag is refused even when it is the only one'

CASE='lower than highest'
run_check VERSION=0.1.5 STUB_REFS=$'refs/tags/desktop-v0.2.0\nrefs/tags/desktop-v0.1.0\n'
expect_refused '0.1.5 is not above the highest desktop release, 0.2.0.'
pass 'a version below the highest tag is refused'

CASE='lower by version order'
run_check VERSION=0.9.0 STUB_REFS=$'refs/tags/desktop-v0.10.0\n'
expect_refused '0.9.0 is not above the highest desktop release, 0.10.0.'
pass '0.9.0 is below 0.10.0'

# --- errors -------------------------------------------------------------------------------------

CASE='API failure'
run_check STUB_GH_FAIL=1
[ "$LAST_STATUS" -eq 2 ] || fail "expected exit 2, got $LAST_STATUS"
grep -qF -- 'the list of desktop-v tags could not be read' "$ERR" || fail 'expected the API failure reason'
pass 'a tag list that cannot be read stops the release (exit 2)'

for name in GITHUB_REF GITHUB_REPOSITORY; do
    CASE="no ${name}"
    run_check "${name}="
    [ "$LAST_STATUS" -eq 2 ] || fail "expected exit 2, got $LAST_STATUS"
    pass "an empty ${name} is a usage error (exit 2)"
done

printf '\nAll %d desktop-release-check case(s) passed.\n' "$cases"
