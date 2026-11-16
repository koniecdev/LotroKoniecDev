#!/usr/bin/env bash
# Test suite for scripts/ci/cd-gate.sh (audit 0001 C2, #1336), CD's CI-success gate.
#
# The GitHub API exists nowhere in CI, so each case runs the gate with a STUB `gh` first on PATH,
# steered by env vars; the stub logs its argv. The property that matters: a workflow_run goes on only
# for a green CI run that a push in this repository started, for a commit that is on main. A fork's
# branch and a tag can both be called `main`, so each of those checks gets its own refusal case, and
# a failed API call refuses too.
#
# CI runs this in the `guards` job, next to the other bash gates.

set -euo pipefail

SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
SCRIPT="$SCRIPTS_DIR/ci/cd-gate.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

cases=0
CASE=""
LAST_STATUS=0
OUT="$TMP_ROOT/stdout"
ERR="$TMP_ROOT/stderr"
CALLS="$TMP_ROOT/calls"
GH_OUT="$TMP_ROOT/github_output"
SUMMARY="$TMP_ROOT/summary"

TESTED_SHA='1111111111111111111111111111111111111111'
MAIN_SHA='2222222222222222222222222222222222222222'
TIP_SHA='3333333333333333333333333333333333333333'

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    printf '  --- stdout ---\n%s\n  --- stderr ---\n%s\n  --- summary ---\n%s\n  --- calls ---\n%s\n' \
        "$(cat "$OUT")" "$(cat "$ERR")" "$(cat "$SUMMARY" 2>/dev/null)" "$(cat "$CALLS" 2>/dev/null)" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# --- Stub ---------------------------------------------------------------------------------------
# STUB_GH_FAIL           — every gh call exits 1
# STUB_MAIN_SHA          — what the branches API names as main's head (default: MAIN_SHA)
# STUB_COMPARE_STATUS    — what the compare API answers (default: ahead)
BIN="$TMP_ROOT/bin"
mkdir -p "$BIN"
cat > "$BIN/gh" <<'STUB'
#!/usr/bin/env bash
printf 'gh %s\n' "$*" >> "$STUB_CALLS"
[ -z "${STUB_GH_FAIL:-}" ] || { echo 'stub: gh api failed' >&2; exit 1; }
case "$2" in
    repos/*/branches/main) printf '%s\n' "${STUB_MAIN_SHA-$MAIN_SHA}" ;;
    repos/*/compare/*) printf '%s\n' "${STUB_COMPARE_STATUS-ahead}" ;;
    *) echo "stub gh: unexpected call '$*'" >&2; exit 1 ;;
esac
STUB
chmod +x "$BIN/gh"

# Runs the gate as a green workflow_run for a push to main in this repository; extra `VAR=value`
# arguments win.
run_gate() {
    : > "$CALLS"
    : > "$GH_OUT"
    : > "$SUMMARY"
    LAST_STATUS=0
    env PATH="$BIN:$PATH" \
        STUB_CALLS="$CALLS" MAIN_SHA="$MAIN_SHA" \
        GITHUB_OUTPUT="$GH_OUT" GITHUB_STEP_SUMMARY="$SUMMARY" \
        GITHUB_REPOSITORY='koniecdev/LotroKoniecDev' \
        EVENT_NAME='workflow_run' \
        FALLBACK_SHA="$TIP_SHA" \
        WR_CONCLUSION='success' \
        WR_BRANCH='main' \
        WR_EVENT='push' \
        WR_HEAD_REPO='koniecdev/LotroKoniecDev' \
        WR_SHA="$TESTED_SHA" \
        WR_ID='42' \
        WR_URL='https://github.com/koniecdev/LotroKoniecDev/actions/runs/42' \
        "$@" \
        "$SCRIPT" > "$OUT" 2> "$ERR" || LAST_STATUS=$?
}

output_is() {
    grep -qx "$1" "$OUT" || fail "stdout is missing '$1'"
    grep -qx "$1" "$GH_OUT" || fail "GITHUB_OUTPUT is missing '$1'"
}

expect_refused() {
    [ "$LAST_STATUS" -eq 0 ] || fail "a refusal is a verdict (exit 0), got $LAST_STATUS"
    output_is 'proceed=false'
    grep -q 'Deploy skipped' "$SUMMARY" || fail 'the summary must say the deploy was skipped'
}

# --- workflow_run that goes on ------------------------------------------------------------------

CASE='commit behind main'
run_gate
[ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
output_is 'proceed=true'
output_is "deploy_sha=$TESTED_SHA"
output_is "short_sha=${TESTED_SHA:0:7}"
grep -qxF "gh api repos/koniecdev/LotroKoniecDev/branches/main --jq .commit.sha" "$CALLS" || fail 'main must be resolved through the branches API'
grep -qxF "gh api repos/koniecdev/LotroKoniecDev/compare/${TESTED_SHA}...${MAIN_SHA} --jq .status" "$CALLS" || fail 'the tested commit must be compared with main by sha'
grep -q 'CI gate passed' "$SUMMARY" || fail 'the summary must say the gate passed'
pass 'a green push to main whose commit main contains goes on, pinned to the tested commit'

CASE='commit is main'
run_gate STUB_COMPARE_STATUS=identical
output_is 'proceed=true'
pass 'a tested commit that is main itself goes on'

# --- workflow_run refusals ----------------------------------------------------------------------

CASE='tag named main'
run_gate STUB_COMPARE_STATUS=diverged
expect_refused
grep -q 'not on main' "$SUMMARY" || fail 'the summary must say the commit is not on main'
pass 'a commit main does not contain (a tag named main) is refused'

CASE='commit ahead of main'
run_gate STUB_COMPARE_STATUS=behind
expect_refused
pass 'a commit newer than main is refused'

CASE='API fails'
run_gate STUB_GH_FAIL=1
expect_refused
pass 'a failed API call refuses: the gate never guesses'

CASE='main sha malformed'
run_gate STUB_MAIN_SHA='main'
expect_refused
if grep -q 'compare/' "$CALLS"; then fail 'a malformed main sha must never reach the compare call'; fi
pass 'a branches answer that is not a full sha refuses before the compare call'

for pair in 'WR_CONCLUSION=failure:CI concluded' 'WR_CONCLUSION=cancelled:CI concluded' \
            'WR_BRANCH=release:not on main' 'WR_EVENT=pull_request:not a push' \
            'WR_HEAD_REPO=someone/LotroKoniecDev:not from this repository'; do
    setting="${pair%%:*}"
    words="${pair#*:}"
    CASE="$setting"
    run_gate "$setting"
    expect_refused
    grep -q "$words" "$SUMMARY" || fail "the summary must give the reason ('$words')"
    [ ! -s "$CALLS" ] || fail 'a run refused on its own fields must not call the API'
    pass "refused with its reason, before any API call"
done

for bad in '' 'abc' "${TESTED_SHA}0" "1111111111111111111111111111111111111111;x" 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'; do
    CASE="tested sha '${bad}'"
    run_gate WR_SHA="$bad"
    expect_refused
    output_is "deploy_sha=$TIP_SHA"
    [ ! -s "$CALLS" ] || fail 'a malformed sha must never reach the API'
    pass 'a tested sha that is not 40 hex is refused, and never reaches the outputs or the API'
done

# --- dispatch and tag push pass through ---------------------------------------------------------

for event in workflow_dispatch push; do
    CASE="$event"
    run_gate EVENT_NAME="$event" WR_CONCLUSION= WR_BRANCH= WR_EVENT= WR_HEAD_REPO= WR_SHA=
    [ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
    output_is 'proceed=true'
    output_is "deploy_sha=$TIP_SHA"
    output_is "short_sha=${TIP_SHA:0:7}"
    [ ! -s "$CALLS" ] || fail 'a dispatch or tag push must not call the API'
    pass 'passes through ungated, pinned to github.sha'
done

# --- usage errors -------------------------------------------------------------------------------

for name in EVENT_NAME FALLBACK_SHA GITHUB_REPOSITORY; do
    CASE="no ${name}"
    run_gate "${name}="
    [ "$LAST_STATUS" -eq 2 ] || fail "expected exit 2, got $LAST_STATUS"
    [ ! -s "$GH_OUT" ] || fail 'a usage error must write no output'
    pass "an empty ${name} is a usage error (exit 2) with no output"
done

printf '\nAll %d cd-gate case(s) passed.\n' "$cases"
