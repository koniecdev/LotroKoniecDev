#!/usr/bin/env bash
# Test suite for the prod-promotion N-1 gate's baseline & span resolution
# (scripts/ci/resolve-prod-baseline.sh — #534).
#
# The script's real inputs — the GitHub deployments API and the prod box's .env — exist nowhere in
# CI, so each case runs it inside a throwaway fixture repo with STUB `gh` and `ssh` binaries first
# on PATH, steered by env vars. Four properties carry the safety story and get the most cases:
#
#   * Fail CLOSED — an unresolvable baseline (API dead + box unreadable, a tag that is no commit,
#     a recorded sha outside history) must block the promotion, never wave it through.
#   * The `inactive` trap — GitHub re-marks a deployment's `success` status `inactive` once a newer
#     deployment succeeds, so a resolver that only reads the LATEST status would find no success on
#     a mature environment and false-bootstrap: the one skip that must stay reserved for a genuinely
#     empty history would fire on every promotion.
#   * The bootstrap skip is the ONLY fail-open verdict, so it must be unreachable while anything is
#     serving. An API window that lists deployments but no `success` (the shape a promotion pause
#     longer than the 100-record window takes on a mature environment) must resolve from the box,
#     never skip: only "the box pins nothing either" earns the bootstrap.
#   * The span must never be NARROWER than reality. The API records the sha of the workflow RUN, so
#     a manual `image_tag` deploy leaves a record naming a commit the box never served; when the
#     box disagrees, the resolver takes the OLDER of the two (the wider span) and an unorderable
#     pair fails closed.
#
# CI runs this in the `guards` job, right next to the other bash gates.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
RESOLVE_SH="$SCRIPTS_DIR/ci/resolve-prod-baseline.sh"
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
    printf '  --- resolve-prod-baseline.sh output ---\n%s\n' "$LAST_OUTPUT" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# --- Fixture repo: a linear history whose migration-free commits sit between the schema ones ---
#   C1 adds src/App/Migrations/..._Init.cs   (expand)
#   C2 adds src/App/Feature.cs               (no schema change)
#   C3 adds src/App/Migrations/..._Drop.cs   (contract)
#   C4 adds src/App/Other.cs                 (no schema change — so C3..C4 is a migration-free span
#                                             while C2..C4 is not: the difference between trusting a
#                                             lying API record and taking the wider span)
FIXTURE="$TMP_ROOT/repo"
mkdir -p "$FIXTURE"
git -C "$FIXTURE" init --quiet --initial-branch=main
git_commit() {
    git -C "$FIXTURE" add -A
    git -C "$FIXTURE" -c user.name=test -c user.email=test@test.invalid commit --quiet -m "$1"
    git -C "$FIXTURE" rev-parse HEAD
}
mkdir -p "$FIXTURE/src/App/Migrations"
echo 'readme' > "$FIXTURE/README.md"
git_commit 'C0: init' >/dev/null
echo 'expand' > "$FIXTURE/src/App/Migrations/20260101000000_Init.cs"
C1="$(git_commit 'C1: expand migration')"
echo 'feature' > "$FIXTURE/src/App/Feature.cs"
C2="$(git_commit 'C2: feature only')"
echo 'contract' > "$FIXTURE/src/App/Migrations/20260102000000_Drop.cs"
C3="$(git_commit 'C3: contract migration')"
echo 'other' > "$FIXTURE/src/App/Other.cs"
C4="$(git_commit 'C4: feature only')"
# A parentless commit: a baseline sharing no history with the candidate cannot be ordered against
# it, and an unorderable pair must fail closed instead of picking a span at random.
UNRELATED="$(git -C "$FIXTURE" -c user.name=test -c user.email=test@test.invalid \
    commit-tree "$(git -C "$FIXTURE" rev-parse 'HEAD^{tree}')" -m 'unrelated root')"
# What the box would pin for each of them — deploy.sh writes IMAGE_TAG=sha-<short> after a roll.
short_c1="$(git -C "$FIXTURE" rev-parse --short=7 "$C1")"
short_c2="$(git -C "$FIXTURE" rev-parse --short=7 "$C2")"
short_c3="$(git -C "$FIXTURE" rev-parse --short=7 "$C3")"

# --- Stubs: `gh` serves JSON fixtures, `ssh` plays the box ------------------------------------
# STUB_GH_FAIL       — every `gh api` call dies (API unusable → box fallback)
# STUB_DEPLOYMENTS   — JSON file served for the deployments list
# STUB_STATUSES_DIR  — dir of <deployment-id>.json files served for each statuses call
# STUB_SSH_FAIL      — the ssh transport dies (fallback must fail CLOSED)
# STUB_BOX_TAG       — what the box .env pins as IMAGE_TAG ('' = nothing pinned yet)
BIN="$TMP_ROOT/bin"
mkdir -p "$BIN"
cat > "$BIN/gh" <<'STUB'
#!/usr/bin/env bash
[ -z "${STUB_GH_FAIL:-}" ] || { echo 'stub: gh api failed' >&2; exit 1; }
url=""
for arg in "$@"; do
    case "$arg" in
        repos/*) url="$arg" ;;
    esac
done
case "$url" in
    */deployments/*/statuses*)
        id="${url#*/deployments/}"
        id="${id%%/*}"
        cat "$STUB_STATUSES_DIR/$id.json"
        ;;
    */deployments\?*)
        cat "$STUB_DEPLOYMENTS"
        ;;
    *)
        echo "stub gh: unexpected url '$url'" >&2
        exit 1
        ;;
esac
STUB
cat > "$BIN/ssh" <<'STUB'
#!/usr/bin/env bash
[ -z "${STUB_SSH_FAIL:-}" ] || { echo 'stub: ssh transport failed' >&2; exit 255; }
[ -z "${STUB_BOX_TAG:-}" ] || printf '%s\n' "$STUB_BOX_TAG"
exit 0
STUB
chmod +x "$BIN/gh" "$BIN/ssh"
PATH="$BIN:$PATH"
export PATH

deployments_json() {
    printf '%s' "$1" > "$TMP_ROOT/deployments.json"
}
statuses_json() {
    mkdir -p "$TMP_ROOT/statuses"
    printf '%s' "$2" > "$TMP_ROOT/statuses/$1.json"
}

# Neutralize the real $GITHUB_OUTPUT / $GITHUB_STEP_SUMMARY (both exist when CI runs this suite)
# with per-case scratch files, so the script under test never pollutes the job's own outputs.
run_resolve() {
    GH_OUT="$TMP_ROOT/github_output"
    : > "$GH_OUT"
    LAST_STATUS=0
    LAST_OUTPUT="$( (cd "$FIXTURE" && env \
        GITHUB_REPOSITORY='koniecdev/LotroKoniecDev' \
        GITHUB_OUTPUT="$GH_OUT" \
        GITHUB_STEP_SUMMARY="$TMP_ROOT/step_summary" \
        STUB_DEPLOYMENTS="$TMP_ROOT/deployments.json" \
        STUB_STATUSES_DIR="$TMP_ROOT/statuses" \
        "$@" "$RESOLVE_SH") 2>&1 )" || LAST_STATUS=$?
}

verdict() { grep -qx "$1" <<<"$LAST_OUTPUT"; }

# --- Usage errors fail closed ------------------------------------------------------------------

CASE='no CANDIDATE_SHA'
run_resolve
[ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 without CANDIDATE_SHA' "got $LAST_STATUS"
pass 'a missing CANDIDATE_SHA is a usage error (exit 2)'

CASE='candidate outside history'
run_resolve CANDIDATE_SHA=0000000000000000000000000000000000000000
[ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 for a candidate sha not in history' "got $LAST_STATUS"
pass 'a candidate sha outside the checkout history is refused (exit 2)'

# --- The API leg -------------------------------------------------------------------------------

CASE='API happy path'
deployments_json "[{\"id\": 11, \"sha\": \"$C3\"}, {\"id\": 10, \"sha\": \"$C2\"}]"
statuses_json 11 '[{"state": "in_progress"}]'
statuses_json 10 '[{"state": "success"}, {"state": "in_progress"}]'
run_resolve CANDIDATE_SHA="$C3"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected a clean resolution' "status $LAST_STATUS"
verdict 'mode=proof' || fail 'a span with a migration must demand the proof'
verdict "baseline=$C2" || fail "expected baseline=$C2 (the last SUCCESSFUL deployment, not the in-flight one)"
grep -qx 'mode=proof' "$GH_OUT" || fail 'mode was not written to GITHUB_OUTPUT'
grep -qx "baseline=$C2" "$GH_OUT" || fail 'baseline was not written to GITHUB_OUTPUT'
pass 'skips the in-flight record, picks the last success, demands the proof for a migration span'

CASE='the inactive trap'
# The LATEST status of the old success IS `inactive` (GitHub re-marks it when a newer deployment
# succeeds); only the full history still contains the `success`. A latest-status-only resolver
# would false-bootstrap here.
deployments_json "[{\"id\": 21, \"sha\": \"$C3\"}, {\"id\": 20, \"sha\": \"$C2\"}]"
statuses_json 21 '[{"state": "in_progress"}]'
statuses_json 20 '[{"state": "inactive"}, {"state": "success"}]'
run_resolve CANDIDATE_SHA="$C3"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected a clean resolution' "status $LAST_STATUS"
verdict "baseline=$C2" || fail 'a deployment whose success was re-marked inactive must still be the baseline'
verdict 'mode=proof' || fail 'the migration span must still demand the proof'
pass 'a success re-marked inactive still resolves (no false bootstrap on a mature environment)'

CASE='rolled-back run skipped'
deployments_json "[{\"id\": 31, \"sha\": \"$C2\"}, {\"id\": 30, \"sha\": \"$C1\"}]"
statuses_json 31 '[{"state": "failure"}, {"state": "in_progress"}]'
statuses_json 30 '[{"state": "inactive"}, {"state": "success"}]'
run_resolve CANDIDATE_SHA="$C3"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected a clean resolution' "status $LAST_STATUS"
verdict "baseline=$C1" || fail 'a deployment that never reached success must be skipped (the box was rolled back)'
pass 'a failed/rolled-back deployment is not the baseline — the one before it is'

CASE='no migrations in span'
deployments_json "[{\"id\": 40, \"sha\": \"$C1\"}]"
statuses_json 40 '[{"state": "success"}]'
run_resolve CANDIDATE_SHA="$C2"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected a clean resolution' "status $LAST_STATUS"
verdict 'mode=skip' || fail 'a migration-free span must skip the proof'
verdict "baseline=$C1" || fail 'the skip verdict should still name the baseline'
pass 'a migration-free promotion span skips the proof (fast path)'

CASE='re-promotion of the serving sha'
deployments_json "[{\"id\": 50, \"sha\": \"$C3\"}]"
statuses_json 50 '[{"state": "success"}]'
run_resolve CANDIDATE_SHA="$C3"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected a clean resolution' "status $LAST_STATUS"
verdict 'mode=skip' || fail 'baseline == candidate must skip (empty span)'
pass 're-promoting the sha already serving is an empty span — skip'

CASE='bootstrap: no deployments'
deployments_json '[]'
run_resolve CANDIDATE_SHA="$C3"
[ "$LAST_STATUS" -eq 0 ] || fail 'the first-ever prod deploy must NOT fail' "status $LAST_STATUS"
verdict 'mode=skip' || fail 'expected the bootstrap skip'
grep -q 'BOOTSTRAP' <<<"$LAST_OUTPUT" || fail 'the bootstrap skip must be loud'
pass 'an empty deployment history is a loud bootstrap skip, never a failure'

CASE='bootstrap: no success yet, box pins nothing'
deployments_json "[{\"id\": 60, \"sha\": \"$C2\"}]"
statuses_json 60 '[{"state": "failure"}]'
run_resolve CANDIDATE_SHA="$C3"
[ "$LAST_STATUS" -eq 0 ] || fail 'a history with no success yet must NOT fail' "status $LAST_STATUS"
verdict 'mode=skip' || fail 'expected the bootstrap skip'
grep -q 'BOOTSTRAP' <<<"$LAST_OUTPUT" || fail 'the bootstrap skip must be loud'
pass 'deployments that never succeeded AND a box pinning nothing is still a bootstrap skip'

CASE='window without a success, but the box knows'
# The fail-open trap this routing closes: on a mature environment every superseded candidate lands
# as `error`, so a promotion pause longer than the API window (measured on this repo: 100 records ≈
# 25 days) leaves a page with no `success` at all — indistinguishable from an empty history to a
# resolver that reads one page. Skipping there waves through the exact batch the gate exists to
# stop, so a non-empty history without a success must resolve from the box instead.
deployments_json "[{\"id\": 61, \"sha\": \"$C3\"}]"
statuses_json 61 '[{"state": "error"}]'
run_resolve CANDIDATE_SHA="$C3" STUB_BOX_TAG="sha-$short_c1"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected the box to answer' "status $LAST_STATUS"
verdict "baseline=$C1" || fail 'a window without a success must resolve from the box, not bootstrap'
verdict 'mode=proof' || fail 'the migration span must demand the proof'
if grep -q 'BOOTSTRAP' <<<"$LAST_OUTPUT"; then fail 'a box that pins a tag must never read as bootstrap'; fi
pass 'a window listing only unsuccessful deployments resolves from the box (no false bootstrap)'

CASE='window without a success and no box either'
run_resolve CANDIDATE_SHA="$C3" STUB_SSH_FAIL=1
[ "$LAST_STATUS" -eq 2 ] || fail 'no success on record + unreadable box must fail CLOSED' "got $LAST_STATUS"
pass 'a window without a success and a dead box fails closed (exit 2)'

CASE='malformed API body'
# `gh api` exits 0 on a body that is not the expected array (an object, a truncated page). That is
# an unusable API, not an empty history — it must route to the box, never to the bootstrap skip.
deployments_json '{"message": "Not Found"}'
run_resolve CANDIDATE_SHA="$C3" STUB_BOX_TAG="sha-$short_c1"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected the box to answer' "status $LAST_STATUS"
verdict "baseline=$C1" || fail 'a malformed API body must fall back to the box'
pass 'a malformed API body falls back to the box instead of bootstrapping'

# --- The cross-check: the API record vs what the box actually serves ---------------------------
# A deployment record carries the sha of the workflow RUN, not of the artifact that was rolled, so
# it can name a commit the box never served. The box `.env` is the ground truth (deploy.sh pins it
# after every successful roll), so when the two disagree the resolver takes the OLDER commit — the
# wider span — and never the narrower one.

CASE='cross-check: box agrees'
deployments_json "[{\"id\": 80, \"sha\": \"$C2\"}]"
statuses_json 80 '[{"state": "success"}]'
run_resolve CANDIDATE_SHA="$C3" STUB_BOX_TAG="sha-$short_c2"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected a clean resolution' "status $LAST_STATUS"
verdict "baseline=$C2" || fail 'agreement must keep the API baseline'
grep -q 'agrees' <<<"$LAST_OUTPUT" || fail 'a successful cross-check should say so'
pass 'the box agreeing with the record keeps the baseline and logs the cross-check'

CASE='cross-check: the box serves an OLDER release than the record claims'
# The manual image_tag path: `sha-<older>` was rolled onto the box, but the deployment record — and
# its `success` — names the run's sha (C3). Trusting the record computes the span C3..C4, which is
# migration-free, and green-lights a batch whose C3 Drop migration the serving release (C2) cannot
# survive. Taking the older commit puts that migration back inside the span.
deployments_json "[{\"id\": 90, \"sha\": \"$C3\"}]"
statuses_json 90 '[{"state": "success"}]'
run_resolve CANDIDATE_SHA="$C4" STUB_BOX_TAG="sha-$short_c2"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected a clean resolution' "status $LAST_STATUS"
verdict "baseline=$C2" || fail 'the older (box) commit must win — the wider span'
verdict 'mode=proof' || fail 'the widened span contains a migration and must demand the proof'
pass 'a record newer than the served artifact is widened to what the box actually serves'

CASE='cross-check: the record alone would have skipped'
# Same inputs, box silent — pins the hole the case above closes: with nothing to cross-check
# against, the record's narrower span is all there is, and it skips.
run_resolve CANDIDATE_SHA="$C4"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected a clean resolution' "status $LAST_STATUS"
verdict "baseline=$C3" || fail 'with no box answer the record stands'
verdict 'mode=skip' || fail 'the record-only span C3..C4 is migration-free'
pass 'the record-only span is the narrower one (documents what the cross-check buys)'

CASE='cross-check: the box serves a NEWER commit than the record'
deployments_json "[{\"id\": 100, \"sha\": \"$C2\"}]"
statuses_json 100 '[{"state": "success"}]'
run_resolve CANDIDATE_SHA="$C4" STUB_BOX_TAG="sha-$short_c3"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected a clean resolution' "status $LAST_STATUS"
verdict "baseline=$C2" || fail 'the older of the two must win regardless of which source it came from'
pass 'a box newer than the record still resolves to the older commit (wider span)'

CASE='cross-check: unorderable pair'
deployments_json "[{\"id\": 110, \"sha\": \"$C2\"}]"
statuses_json 110 '[{"state": "success"}]'
run_resolve CANDIDATE_SHA="$C4" STUB_BOX_TAG="sha-$UNRELATED"
[ "$LAST_STATUS" -eq 2 ] || fail 'two baselines with no ancestry must fail CLOSED' "got $LAST_STATUS"
pass 'a record and a box tag on unrelated histories fail closed (exit 2)'

CASE='cross-check: an unreadable box is not a new failure mode'
# The cross-check is hardening on top of a resolved baseline; it must never turn a working
# promotion red on its own. ssh already proved itself in the step before this gate.
deployments_json "[{\"id\": 120, \"sha\": \"$C2\"}]"
statuses_json 120 '[{"state": "success"}]'
run_resolve CANDIDATE_SHA="$C3" STUB_SSH_FAIL=1
[ "$LAST_STATUS" -eq 0 ] || fail 'a failed cross-check must not block a resolved baseline' "status $LAST_STATUS"
verdict "baseline=$C2" || fail 'the API baseline must survive an unreadable box'
pass 'an unreadable box warns and keeps the API baseline (hardening, not a new gate)'

CASE='recorded sha outside history'
deployments_json '[{"id": 70, "sha": "1111111111111111111111111111111111111111"}]'
statuses_json 70 '[{"state": "success"}]'
run_resolve CANDIDATE_SHA="$C3"
[ "$LAST_STATUS" -eq 2 ] || fail 'a baseline sha outside the checkout history must fail CLOSED' "got $LAST_STATUS"
pass 'a recorded baseline the checkout cannot see fails closed (exit 2)'

# --- The box fallback (API unusable) -----------------------------------------------------------

CASE='fallback: box tag resolves'
run_resolve CANDIDATE_SHA="$C3" STUB_GH_FAIL=1 STUB_BOX_TAG="sha-$short_c2"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected the box fallback to resolve' "status $LAST_STATUS"
verdict "baseline=$C2" || fail "expected the box-pinned sha-$short_c2 to resolve to $C2"
verdict 'mode=proof' || fail 'the migration span must demand the proof'
pass "a dead API falls back to the box IMAGE_TAG (sha-$short_c2 → baseline)"

CASE='fallback: nothing pinned'
run_resolve CANDIDATE_SHA="$C3" STUB_GH_FAIL=1
[ "$LAST_STATUS" -eq 0 ] || fail 'first-ever deploy with a dead API must NOT fail' "status $LAST_STATUS"
verdict 'mode=skip' || fail 'expected the bootstrap skip'
grep -q 'BOOTSTRAP' <<<"$LAST_OUTPUT" || fail 'the bootstrap skip must be loud'
pass 'a box with no IMAGE_TAG pinned is the first-ever deploy — loud bootstrap skip'

CASE='fallback: unresolvable tag'
run_resolve CANDIDATE_SHA="$C3" STUB_GH_FAIL=1 STUB_BOX_TAG='sha-deadbee'
[ "$LAST_STATUS" -eq 2 ] || fail 'a box tag that is no commit must fail CLOSED' "got $LAST_STATUS"
pass 'a box tag that resolves to no commit fails closed (exit 2)'

CASE='fallback: hostile tag'
run_resolve CANDIDATE_SHA="$C3" STUB_GH_FAIL=1 STUB_BOX_TAG='sha-abc; rm -rf /'
[ "$LAST_STATUS" -eq 2 ] || fail 'a tag outside [A-Za-z0-9._-] must be rejected' "got $LAST_STATUS"
pass 'a hostile box tag is rejected before it reaches git (exit 2)'

CASE='fallback: ssh dead too'
run_resolve CANDIDATE_SHA="$C3" STUB_GH_FAIL=1 STUB_SSH_FAIL=1
[ "$LAST_STATUS" -eq 2 ] || fail 'API dead + box unreadable must fail CLOSED, not wave the batch through' "got $LAST_STATUS"
pass 'both sources dead fails closed (exit 2) — the gate never guesses'

printf '\nAll %d resolve-prod-baseline case(s) passed.\n' "$cases"
