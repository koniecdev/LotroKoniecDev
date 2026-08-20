#!/usr/bin/env bash
# Test suite for the prod-promotion N-1 gate's verdict mapping
# (scripts/ci/n1-promotion-gate.sh — #534).
#
# The one property here is the operator instruction attached to each exit code of
# scripts/n1-compat.sh. That script separates "the serving release cannot live on this schema"
# (exit 1) from "the proof never ran" (exit 2 — dotnet tool restore, script generation, the
# worktree, a missing seam, no integration suites found, or a serving release that no longer
# restores/builds or executed zero tests — #679; scripts/tests/n1-compat.tests.sh pins that side). Both block the promotion, but they demand
# OPPOSITE next actions: exit 1 means do NOT retry, promote in smaller steps; exit 2 means the batch
# is UNJUDGED and the fix is the infra, not the batch. Collapsing them into one "your migrations
# break prod" message sends the approver to split a healthy batch — or to the `image_tag` dispatch
# that skips this gate entirely, which is the worst outcome the gate can produce.
#
# Each case runs the real gate script inside a fixture tree with a STUB scripts/n1-compat.sh, so the
# mapping is exercised without Docker, .NET or a schema. The gate resolves the proof relative to its
# own location, which is exactly what the fixture reproduces — no production test seam.
#
# CI runs this in the `guards` job, right next to the other bash gates.

set -euo pipefail

GATE_SH="$(cd "$(dirname "$0")/.." && pwd)/ci/n1-promotion-gate.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

cases=0
CASE=""
LAST_OUTPUT=""
LAST_STATUS=0

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    printf '  --- n1-promotion-gate.sh output ---\n%s\n' "$LAST_OUTPUT" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# --- Fixture: the gate next to a stub proof ----------------------------------------------------
# STUB_N1_EXIT — the exit code the stubbed n1-compat.sh reports.
# The stub records its argv so a case can prove the baseline was passed through verbatim (and that
# it was not invoked at all when the gate refuses up front).
FIXTURE="$TMP_ROOT/repo"
ARGS_FILE="$TMP_ROOT/n1-compat.args"
mkdir -p "$FIXTURE/scripts/ci"
cp "$GATE_SH" "$FIXTURE/scripts/ci/n1-promotion-gate.sh"
cat > "$FIXTURE/scripts/n1-compat.sh" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$@" > "$N1_ARGS_FILE"
echo "stub n1-compat: pretending to prove ${1:-<no baseline>}"
exit "${STUB_N1_EXIT:-0}"
STUB
chmod +x "$FIXTURE/scripts/n1-compat.sh"

run_gate() {
    : > "$ARGS_FILE"
    LAST_STATUS=0
    LAST_OUTPUT="$( (cd "$FIXTURE" && env \
        GITHUB_STEP_SUMMARY="$TMP_ROOT/step_summary" \
        N1_ARGS_FILE="$ARGS_FILE" \
        "$@" ./scripts/ci/n1-promotion-gate.sh) 2>&1 )" || LAST_STATUS=$?
}

BASELINE='1f09af59d1bc0394da22452c4e6f89c6758e0ea4'

CASE='proof green'
run_gate BASELINE_SHA="$BASELINE" STUB_N1_EXIT=0
[ "$LAST_STATUS" -eq 0 ] || fail 'a green proof must let the promotion proceed' "status $LAST_STATUS"
grep -q 'GREEN' <<<"$LAST_OUTPUT" || fail 'the green verdict should be stated'
grep -qx "$BASELINE" "$ARGS_FILE" || fail 'the baseline must reach n1-compat.sh verbatim, as its first argument' "$(cat "$ARGS_FILE")"
pass 'a green proof exits 0 and passes the baseline through verbatim'

CASE='proof red'
run_gate BASELINE_SHA="$BASELINE" STUB_N1_EXIT=1
[ "$LAST_STATUS" -eq 1 ] || fail 'an incompatible schema must block with exit 1' "status $LAST_STATUS"
grep -q '::error::' <<<"$LAST_OUTPUT" || fail 'the block must be an annotated error'
grep -q 'RED' <<<"$LAST_OUTPUT" || fail 'the red verdict should be stated'
grep -q "$BASELINE" <<<"$LAST_OUTPUT" || fail 'the failure must name the baseline sha'
grep -qi 'smaller steps' <<<"$LAST_OUTPUT" || fail 'the red verdict must carry the expand/contract resolution'
if grep -qi 'UNJUDGED' <<<"$LAST_OUTPUT"; then fail 'a real incompatibility must NOT read as an infra failure'; fi
pass 'exit 1 blocks with the RED verdict, the baseline and the promote-in-smaller-steps resolution'

CASE='proof could not run'
run_gate BASELINE_SHA="$BASELINE" STUB_N1_EXIT=2
[ "$LAST_STATUS" -eq 2 ] || fail 'an unrunnable proof must block, and keep its own exit code' "status $LAST_STATUS"
grep -q '::error::' <<<"$LAST_OUTPUT" || fail 'the block must be an annotated error'
grep -q 'UNJUDGED' <<<"$LAST_OUTPUT" || fail 'the operator must be told the batch is unjudged, not proven bad'
if grep -qi 'smaller steps' <<<"$LAST_OUTPUT"; then fail 'an infra failure must NOT advise splitting the batch'; fi
if grep -qw 'RED' <<<"$LAST_OUTPUT"; then fail 'an infra failure must not be reported as a schema RED'; fi
pass 'exit 2 blocks as UNJUDGED — no split-the-batch advice on an infra failure'

CASE='proof died in an unforeseen way'
run_gate BASELINE_SHA="$BASELINE" STUB_N1_EXIT=127
[ "$LAST_STATUS" -eq 2 ] || fail 'any other exit code must fail closed as unrunnable' "status $LAST_STATUS"
grep -q 'UNJUDGED' <<<"$LAST_OUTPUT" || fail 'unknown exit codes join the unjudged bucket'
pass 'an unknown exit code fails closed as unjudged (never a silent pass)'

CASE='no baseline'
run_gate STUB_N1_EXIT=0
[ "$LAST_STATUS" -eq 2 ] || fail 'a missing BASELINE_SHA must fail closed' "got $LAST_STATUS"
[ ! -s "$ARGS_FILE" ] || fail 'n1-compat.sh must not run without a baseline' "$(cat "$ARGS_FILE")"
pass 'a missing BASELINE_SHA fails closed and never invokes the proof'

CASE='step summary'
run_gate BASELINE_SHA="$BASELINE" STUB_N1_EXIT=2
grep -q 'UNJUDGED' "$TMP_ROOT/step_summary" || fail 'the approver reads the summary — the block must land there too'
pass 'a blocked promotion writes its verdict to the job summary'

printf '\nAll %d n1-promotion-gate case(s) passed.\n' "$cases"
