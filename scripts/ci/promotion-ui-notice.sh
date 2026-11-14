#!/usr/bin/env bash
#
# Does this prod promotion change what a user sees? (#1377)
#
# Automated tests do not notice that a screen is confusing, so a promotion that changes the UI
# should get a /staging-walk before the approval click. This script tells the approver. It diffs
# the release prod runs now against the candidate and looks for UI files (UI_PATTERN below, the
# one place that defines them). The prod leg of .github/workflows/deploy.yml runs it in a job
# that starts before the approval gate. A session can run it locally too:
#
#   CANDIDATE_SHA=origin/main scripts/ci/promotion-ui-notice.sh
#
# The baseline is the newest `production` deployment that ever reached `success`, the rule of
# resolve_via_api in resolve-prod-baseline.sh, read with --paginate as image-scan.yml does. There
# is no box cross-check: this job has no `production` environment, so it has no ssh. A deploy by
# hand on the box leaves no record, so the baseline can be older than what prod runs.
#
# Advisory only: it always exits 0. When it cannot tell, it says so and asks for a walk to be safe.
#
# Verdict on stdout (and in $GITHUB_OUTPUT when set): ui_change=yes|no|unknown, baseline=<sha|''>.
# In GitHub Actions ($GITHUB_ACTIONS=true) it adds a ::warning:: annotation for `yes` and
# `unknown`, and writes the details to $GITHUB_STEP_SUMMARY. Locally the details go to stderr.
#
# Env: CANDIDATE_SHA (default HEAD), GITHUB_REPOSITORY (default: `gh repo view`), GH_TOKEN with
# `deployments: read`, DEPLOY_ENVIRONMENT (default production), IMAGE_TAG and SHORT_SHA (the
# explicit image_tag input of cd.yml's dispatch, and the candidate's short sha).
#
# Tests: scripts/tests/promotion-ui-notice.tests.sh. CI-only bash, like the other scripts/ci/ files.
set -uo pipefail
export LC_ALL=C

# Files a user sees: every Frontend file, the auth Razor Pages and static files, the e-mails the
# auth app sends, its error pages, and its CSP (it decides what an auth page may load). A false
# positive costs one suggested walk.
UI_PATTERN='^src/Frontend/|^src/AuthSystem/.*/Pages/|^src/AuthSystem/.*/wwwroot/|^src/AuthSystem/.*/Services/Emails/Templates/|^src/AuthSystem/.*/Services/Emails/[^/]*EmailSender\.cs$|^src/AuthSystem/.*/Middleware/([^/]*Page[^/]*|SecurityHeadersMiddleware)\.cs$'
ANNOTATION_FILE_LIMIT=20
ASK='ask a session for /staging-walk before you approve'

in_actions() { [ "${GITHUB_ACTIONS:-}" = 'true' ]; }

details() {
    if in_actions && [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
        printf '%s\n' "$@" >> "$GITHUB_STEP_SUMMARY"
    else
        printf '%s\n' "$@" >&2
    fi
}

emit() {
    local verdicts
    verdicts="$(printf 'ui_change=%s\nbaseline=%s' "$1" "$2")"
    printf '%s\n' "$verdicts"
    if [ -n "${GITHUB_OUTPUT:-}" ]; then
        printf '%s\n' "$verdicts" >> "$GITHUB_OUTPUT"
    fi
}

# Workflow-command escaping: `%` first, or the escapes added after it would be escaped again.
escape_annotation() {
    local text="$1"
    text="${text//'%'/%25}"
    text="${text//$'\r'/%0D}"
    text="${text//$'\n'/%0A}"
    printf '%s' "$text"
}

annotate() {
    if in_actions; then
        printf '::warning title=%s::%s\n' "$1" "$(escape_annotation "$2")"
    fi
}

cannot_tell() {
    annotate 'Cannot tell whether this promotion changes the UI' "${1} To be safe, ${ASK}."
    details '## UI change notice' '' \
        "**Cannot tell whether this promotion changes the UI.** ${1}" '' \
        "To be safe, ${ASK}."
    emit unknown "${2:-}"
    exit 0
}

# rc 0 = baseline echoed; 1 = the API could not answer; 2 = no record ever reached `success`.
resolve_baseline() {
    local records id sha states
    records="$(gh api --paginate "repos/${repository}/deployments?environment=${environment}&per_page=100" 2>/dev/null \
        | jq -r '.[] | "\(.id) \(.sha)"' 2>/dev/null)" || return 1
    while read -r id sha; do
        [ -n "$id" ] || continue
        states="$(gh api "repos/${repository}/deployments/${id}/statuses?per_page=100" 2>/dev/null \
            | jq -r '.[].state' 2>/dev/null)" || return 1
        if grep -qx 'success' <<< "$states"; then
            printf '%s\n' "$sha"
            return 0
        fi
    done <<< "$records"
    return 2
}

environment="${DEPLOY_ENVIRONMENT:-production}"
image_tag="${IMAGE_TAG:-}"

if [ -n "$image_tag" ] && [ "$image_tag" != "sha-${SHORT_SHA:-}" ]; then
    cannot_tell "This run rolls the explicit image tag \`${image_tag}\`, not the build of this commit."
fi

candidate="$(git rev-parse --verify --quiet "${CANDIDATE_SHA:-HEAD}^{commit}")" \
    || cannot_tell "The candidate \`${CANDIDATE_SHA:-HEAD}\` is not a commit in this checkout."

repository="${GITHUB_REPOSITORY:-}"
if [ -z "$repository" ]; then
    repository="$(gh repo view --json nameWithOwner --jq .nameWithOwner 2>/dev/null)" \
        || cannot_tell 'The repository could not be found (set GITHUB_REPOSITORY).'
fi

baseline="$(resolve_baseline)"
case $? in
    0) ;;
    2) cannot_tell "No \`${environment}\` deployment ever reached success, so it is unknown what prod runs." ;;
    *) cannot_tell "The GitHub deployments API did not answer, so it is unknown what prod runs." ;;
esac

git cat-file -e "${baseline}^{commit}" 2>/dev/null \
    || cannot_tell "Prod's release \`${baseline}\` is not a commit in this checkout." "$baseline"

# --no-renames: a UI file moved out of a UI folder still counts as a UI change.
changed="$(git -c core.quotePath=false diff --no-renames --name-only "$baseline" "$candidate")" \
    || cannot_tell "git could not diff prod's release \`${baseline}\` against the candidate." "$baseline"
ui_files="$(grep -E "$UI_PATTERN" <<< "$changed")"

source_line="Prod runs commit \`${baseline}\` (the newest successful \`${environment}\` deployment record, not checked against the box)."
timing_line="Worked out when this run started. If another promotion was approved since, run \`CANDIDATE_SHA=${candidate} scripts/ci/promotion-ui-notice.sh\` locally for the current answer."
candidate_line="Candidate: commit \`${candidate}\`."

if [ -z "$ui_files" ]; then
    details '## UI change notice' '' "This promotion changes no UI file. No walk is needed." '' \
        "$candidate_line" "$source_line" "$timing_line"
    emit no "$baseline"
    exit 0
fi

count="$(grep -c '' <<< "$ui_files")"
listed="$(head -n "$ANNOTATION_FILE_LIMIT" <<< "$ui_files")"
message="This promotion changes the UI: ${ASK}. ${count} UI files changed since prod's release ${baseline:0:7}:"$'\n'"${listed}"
if [ "$count" -gt "$ANNOTATION_FILE_LIMIT" ]; then
    message="${message}"$'\n'"…and $((count - ANNOTATION_FILE_LIMIT)) more: see the job summary."
fi
annotate 'This promotion changes the UI' "$message"

details '## UI change notice' '' \
    "**This promotion changes the UI: ${ASK}.**" '' \
    "$candidate_line" "$source_line" "$timing_line" '' \
    "UI files changed (${count}):" ''
while IFS= read -r file; do
    details "- \`${file}\`"
done <<< "$ui_files"
details '' 'Not detected: a screen that changes only because an API answer changed (the links an API offers, the Contracts, the Polish error texts the APIs send).'

emit yes "$baseline"
exit 0
