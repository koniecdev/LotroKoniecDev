#!/usr/bin/env bash
#
# Prod-promotion N-1 gate — baseline & span resolution (#534).
#
# Prod promotion is batched: staging deploys every push to main, prod ships every Nth candidate
# behind the approval gate. That breaks the assumption the migration safety story was built on —
# "previous merge == previous deploy". The PR-time proof (n1-compat.yml) checks HEAD^1, i.e. the
# previous MERGE; ADR-0023's expand → backfill → contract "across ≥ 2 deploys" is a property of
# DEPLOYS. An expand and its contract can ride one approved batch with every per-step check green
# while the release still serving on prod breaks on the dropped column. So at promotion time the
# executable proof (ADR-0024, scripts/n1-compat.sh — it already accepts an arbitrary baseline ref)
# must run against the sha the box is ACTUALLY serving.
#
# This script resolves that baseline and classifies the promotion span; the prod leg of
# .github/workflows/deploy.yml then runs the proof (scripts/ci/n1-promotion-gate.sh) only when the
# span touches Migrations/ files. Resolution order:
#
#   1. GitHub deployments API — the newest deployment of $DEPLOY_ENVIRONMENT that ever reached a
#      `success` status. The HISTORY matters: when a newer deployment succeeds, GitHub re-marks
#      the previous success `inactive`, so "latest status == success" would find nothing and
#      false-bootstrap on a mature environment. A run that failed and rolled back never reached
#      `success` and is skipped; the in-flight run's own record is `in_progress` and is skipped
#      too, so re-promoting a sha that already served resolves to itself (empty span, fast skip).
#   2. The box — the IMAGE_TAG scripts/hetzner/deploy.sh pins in the box .env after every successful
#      roll (`sha-<short>` → commit, `v*` tag → commit). Consulted when the API cannot answer, AND
#      as a cross-check when it can (step 3).
#   3. Cross-check. A deployment record carries the sha of the workflow RUN, not of the artifact
#      that was rolled: a `workflow_dispatch` with an explicit image_tag rolls one commit while its
#      record (and its `success`) names another. The box .env is the ground truth, so when the two
#      disagree this takes the OLDER commit — the WIDER span — and an unorderable pair (unrelated
#      histories) fails closed. Never the narrower span: that is how a migration goes unseen.
#      The cross-check is hardening on top of a resolved baseline and never blocks on its own — an
#      unreadable box warns and keeps the API's answer (ssh already proved itself one step earlier).
#
# The single fail-OPEN verdict is bootstrap, so it stays reserved for "nothing is serving": either
# the environment has no deployment record at all, or the box pins no tag. A window that lists
# deployments without a `success` is NOT that — on a mature environment every superseded candidate
# lands as `error`, so a promotion pause longer than the API window (measured 2026-07-27: 100
# records ≈ 25 days) takes exactly that shape — and it routes to the box instead of skipping.
#
# Verdicts (stdout `key=value` + $GITHUB_OUTPUT when set; human detail on stderr):
#   mode=proof  baseline=<sha>   — Migrations/ files inside <baseline>..<candidate>: run the proof.
#   mode=skip   baseline=<sha|''> — no migrations in the span, or bootstrap (nothing serving).
#
# Exit codes: 0 = verdict emitted, 2 = cannot resolve. Fail CLOSED: an unresolvable baseline
# blocks the promotion — a gate that cannot compute its input must not wave a batch through.
#
# Env: CANDIDATE_SHA (required — the pinned deploy sha, checked out as HEAD with full history),
# GITHUB_REPOSITORY + GH_TOKEN with `deployments: read` (the API leg), DEPLOY_ENVIRONMENT
# (default production), STACK_DIR (default /opt/lotro — the box .env location).
# The `ssh box` alias comes from deploy.yml's "Configure ssh to the box" step, which runs before
# this gate exactly so the box legs have a transport.
#
# Tests: scripts/tests/resolve-prod-baseline.tests.sh (stub `gh` + `ssh` over a fixture repo; CI
# runs them in the guards job). CI-only, Linux runners — no .ps1 twin, same as classify-changes.sh.
set -euo pipefail

# Same Migrations-directory shape as check-migration-safety.sh — deliberately broader than
# n1-compat.yml's `src/**/Migrations/**` path filter: a false positive costs one redundant proof,
# a false negative waves a schema change past the gate.
MIGRATIONS_PATTERN='(^|/)Migrations/'

log() { echo "$*" >&2; }

summary() {
    if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
        echo "$*" >> "$GITHUB_STEP_SUMMARY"
    fi
}

emit() {
    verdicts="$(printf 'mode=%s\nbaseline=%s' "$1" "$2")"
    printf '%s\n' "$verdicts"
    if [ -n "${GITHUB_OUTPUT:-}" ]; then
        printf '%s\n' "$verdicts" >> "$GITHUB_OUTPUT"
    fi
}

bootstrap_skip() {
    msg="N-1 promotion gate: BOOTSTRAP — ${1} Nothing is serving that this batch's migrations could break, so the deploy-time proof is skipped (#534); the window closes at the first successful deploy."
    log "$msg"
    summary "$msg"
    emit skip ""
    exit 0
}

# rc 0 = baseline echoed; 3 = the environment has no deployment record at all (bootstrap candidate);
# 4 = API unusable; 5 = records exist but none ever reached `success` (NOT bootstrap — see header).
resolve_via_api() {
    local list_json count i id sha states
    list_json="$(gh api "repos/${GITHUB_REPOSITORY}/deployments?environment=${DEPLOY_ENVIRONMENT}&per_page=100" 2>/dev/null)" || return 4
    count="$(printf '%s' "$list_json" | jq 'length' 2>/dev/null)" || return 4
    # A body that is not the expected array (an object, a truncated page) yields a `length` that is
    # either non-numeric or a key count — an unusable API, never an empty history.
    case "$count" in
        '' | *[!0-9]*) return 4 ;;
    esac
    [ "$count" -gt 0 ] || return 3
    for i in $(seq 0 $((count - 1))); do
        id="$(printf '%s' "$list_json" | jq -r ".[$i].id")" || return 4
        sha="$(printf '%s' "$list_json" | jq -r ".[$i].sha")" || return 4
        states="$(gh api "repos/${GITHUB_REPOSITORY}/deployments/${id}/statuses?per_page=100" 2>/dev/null)" || return 4
        if printf '%s' "$states" | jq -r '.[].state' 2>/dev/null | grep -qx 'success'; then
            printf '%s\n' "$sha"
            return 0
        fi
    done
    return 5
}

# rc 0 = baseline echoed; 3 = no IMAGE_TAG pinned yet (bootstrap candidate); 2 = hard failure.
resolve_via_box() {
    local tag ref sha
    # `cat … 2>/dev/null | sed` (not `sed … file`): a box without the .env yet — the first-ever
    # deploy — must read as "no tag pinned" (bootstrap), while a dead ssh transport stays a
    # hard failure. The remote shell has no pipefail, so the pipeline's status is sed's.
    # SC2029: the RUNNER must expand ${STACK_DIR} into the remote command — the box has no such
    # variable. Intentional (same convention as deploy.yml's ssh steps).
    # shellcheck disable=SC2029
    tag="$(ssh box "cat ${STACK_DIR}/.env 2>/dev/null | sed -n 's/^IMAGE_TAG=//p'" </dev/null | tail -1 | tr -d '\r')" || {
        log "ERROR: could not read IMAGE_TAG from the box over ssh."
        return 2
    }
    [ -n "$tag" ] || return 3
    case "$tag" in
        *[!A-Za-z0-9._-]*)
            log "ERROR: the box .env pins IMAGE_TAG '${tag}', which is not a legal image tag."
            return 2
            ;;
    esac
    case "$tag" in
        sha-*) ref="${tag#sha-}" ;;
        *)     ref="$tag" ;;
    esac
    if ! sha="$(git rev-parse --verify --quiet "${ref}^{commit}")"; then
        log "ERROR: cannot resolve the box-pinned IMAGE_TAG '${tag}' to a commit (tried '${ref}^{commit}')."
        log "Refusing to guess what is serving (fail closed)."
        return 2
    fi
    printf '%s\n' "$sha"
}

# Sets $baseline + $baseline_source from the box, or exits (bootstrap skip / fail closed).
# $1 = why the API leg could not answer, for the bootstrap message.
use_box_leg() {
    local box_rc=0
    baseline="$(resolve_via_box)" || box_rc=$?
    case "$box_rc" in
        0) baseline_source="IMAGE_TAG pinned in the box .env" ;;
        3) bootstrap_skip "${1} and the box pins no IMAGE_TAG yet (first-ever deploy)." ;;
        *) exit 2 ;;
    esac
}

# Widens $baseline to the box's answer when the two disagree; see header step 3.
cross_check_with_box() {
    local box_baseline box_rc=0
    box_baseline="$(resolve_via_box)" || box_rc=$?
    case "$box_rc" in
        0) ;;
        3)
            log "NOTE: the box pins no IMAGE_TAG — nothing to cross-check the recorded baseline against."
            return 0
            ;;
        *)
            log "WARNING: could not read the box IMAGE_TAG to cross-check the recorded baseline — proceeding with the API's answer."
            return 0
            ;;
    esac
    if [ "$box_baseline" = "$baseline" ]; then
        log "Cross-check: the IMAGE_TAG pinned on the box agrees with the deployments API."
        return 0
    fi
    log "WARNING: the deployments API and the box disagree about what is serving — API ${baseline}, box ${box_baseline}."
    log "A deployment record names the sha of the workflow RUN, so a manual image_tag deploy leaves it pointing at a commit the box never served. Taking the OLDER commit (the wider span)."
    if git merge-base --is-ancestor "$box_baseline" "$baseline"; then
        baseline="$box_baseline"
        baseline_source="IMAGE_TAG pinned in the box .env (older than the recorded deployment — widened)"
    elif git merge-base --is-ancestor "$baseline" "$box_baseline"; then
        baseline_source="${baseline_source} (older than the box-pinned tag — kept)"
    else
        log "ERROR: neither baseline is an ancestor of the other — the two sources sit on unrelated histories, so the span cannot be computed (fail closed)."
        log "Resolve by hand: confirm what the box is running (${STACK_DIR}/.env → IMAGE_TAG) and re-deploy that sha before promoting."
        exit 2
    fi
}

if [ -z "${CANDIDATE_SHA:-}" ]; then
    log "ERROR: CANDIDATE_SHA is required (the pinned deploy sha)."
    exit 2
fi
if ! git cat-file -e "${CANDIDATE_SHA}^{commit}" 2>/dev/null; then
    log "ERROR: candidate ${CANDIDATE_SHA} is not in this checkout's history."
    exit 2
fi

DEPLOY_ENVIRONMENT="${DEPLOY_ENVIRONMENT:-production}"
STACK_DIR="${STACK_DIR:-/opt/lotro}"

baseline=""
baseline_source=""
api_rc=0
if [ -n "${GITHUB_REPOSITORY:-}" ]; then
    baseline="$(resolve_via_api)" || api_rc=$?
else
    log "GITHUB_REPOSITORY is not set — the deployments API leg cannot run."
    api_rc=4
fi

case "$api_rc" in
    0)
        baseline_source="last successful '${DEPLOY_ENVIRONMENT}' deployment (GitHub deployments API)"
        ;;
    3)
        bootstrap_skip "no '${DEPLOY_ENVIRONMENT}' deployment is on record at all."
        ;;
    4)
        log "WARNING: the deployments API is unusable — resolving from the IMAGE_TAG pinned on the box instead."
        use_box_leg "the deployments API is unusable"
        ;;
    5)
        log "WARNING: the API lists '${DEPLOY_ENVIRONMENT}' deployments but none ever reached 'success' — that is what a promotion pause longer than the API window looks like, NOT an empty history. Resolving from the IMAGE_TAG pinned on the box instead."
        use_box_leg "no '${DEPLOY_ENVIRONMENT}' deployment on record ever reached 'success'"
        ;;
    *)
        exit 2
        ;;
esac

if ! git cat-file -e "${baseline}^{commit}" 2>/dev/null; then
    log "ERROR: baseline ${baseline} (${baseline_source}) is not in this checkout's history."
    log "Refusing to guess the span (fail closed) — is the checkout fetch-depth: 0?"
    exit 2
fi

if [ "$api_rc" -eq 0 ]; then
    cross_check_with_box
fi

log "Serving baseline: ${baseline} — ${baseline_source}"

if [ "$baseline" = "$CANDIDATE_SHA" ]; then
    msg="N-1 promotion gate: candidate ${CANDIDATE_SHA} is the sha already serving (re-promotion) — empty span, skipping the proof."
    log "$msg"
    summary "$msg"
    emit skip "$baseline"
    exit 0
fi

if ! span_files="$(git diff --name-only "$baseline" "$CANDIDATE_SHA")"; then
    log "ERROR: git diff ${baseline}..${CANDIDATE_SHA} failed — cannot classify the span (fail closed)."
    exit 2
fi
migration_files="$(printf '%s\n' "$span_files" | grep -E "$MIGRATIONS_PATTERN" || true)"

if [ -z "$migration_files" ]; then
    msg="N-1 promotion gate: no Migrations/ files in ${baseline} → ${CANDIDATE_SHA} — schema unchanged since the serving release; skipping the proof."
    log "$msg"
    summary "$msg"
    emit skip "$baseline"
    exit 0
fi

log "Migrations inside the promotion span ${baseline} → ${CANDIDATE_SHA}:"
printf '%s\n' "$migration_files" | sed 's/^/  /' >&2
msg="N-1 promotion gate: the span since the serving release touches $(printf '%s\n' "$migration_files" | wc -l | tr -d ' ') Migrations/ file(s) — proving release ${baseline} against the candidate schema (ADR-0024)."
log "$msg"
summary "$msg"
emit proof "$baseline"
