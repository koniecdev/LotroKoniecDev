#!/usr/bin/env bash
#
# Prod-promotion N-1 gate — run the ADR-0024 proof against the serving release and turn its exit
# code into the right operator instruction (#534).
#
# scripts/n1-compat.sh keeps two failures apart on purpose:
#   exit 1 — the serving release CANNOT live on this batch's schema. The batch is proven bad.
#            Retrying changes nothing; the resolution is to promote in smaller steps (ADR-0023:
#            approve a candidate carrying only the expand, let it serve, then promote the contract).
#   exit 2 — the proof never ran (dotnet tool restore, schema-script generation, the worktree, a
#            missing seam, no integration suites found, or a serving release that no longer
#            restores/builds or that executed zero tests — #679). The batch is UNJUDGED, not bad;
#            the fix is the infra failure in the log, and the promotion is blocked because an
#            unjudged batch must not ship — not because its migrations are known to break anything.
#
# Collapsing the two sends the approver to split a healthy batch on an infra flake — or worse, to
# the `image_tag` dispatch, which skips this gate entirely. So the mapping lives here, in a script
# CI executes and the self-test pins, instead of inline in the workflow where nothing checks it.
#
# Env: BASELINE_SHA (required — the serving release resolved by scripts/ci/resolve-prod-baseline.sh).
# Exit codes: 0 = the promotion may proceed, 1 = blocked, schema is incompatible,
# 2 = blocked, the proof could not run (fail closed).
#
# Tests: scripts/tests/n1-promotion-gate.tests.sh (stub n1-compat.sh over a fixture tree; CI runs
# them in the guards job). CI-only, Linux runners — no .ps1 twin, same as classify-changes.sh.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"

say() {
    echo "$1"
    if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
        echo "$1" >> "$GITHUB_STEP_SUMMARY"
    fi
}

if [ -z "${BASELINE_SHA:-}" ]; then
    say "::error::N-1 promotion gate: BASELINE_SHA is empty — refusing to run the proof against an unknown baseline (fail closed)."
    exit 2
fi

status=0
"$repo_root/scripts/n1-compat.sh" "$BASELINE_SHA" || status=$?

case "$status" in
    0)
        say "N-1 promotion gate: GREEN — the release serving on prod (${BASELINE_SHA}) survives this batch's schema."
        ;;
    1)
        say "::error::N-1 promotion gate RED — this batch's migrations break the release currently serving on prod (baseline ${BASELINE_SHA}; the failing suites are in the log above). Do NOT retry: promote in smaller steps — first approve a candidate containing only the expand, let it serve, then promote the contract (ADR-0023)."
        exit 1
        ;;
    *)
        say "::error::N-1 promotion gate: the proof COULD NOT RUN (n1-compat.sh exit ${status}) against baseline ${BASELINE_SHA}, so this batch is UNJUDGED — not proven bad. Blocking the promotion (fail closed). Fix the infra failure in the log above and re-run this job; splitting the batch or dispatching a manual image_tag (which skips this gate) would only hide the fact that nothing was proven."
        exit 2
        ;;
esac
