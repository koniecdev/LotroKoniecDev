#!/usr/bin/env bash
#
# CD's CI-success gate (audit 0001 C2, #1336): decides whether a CD run may build and deploy, and
# pins the commit it builds. cd.yml's `gate` job runs it.
#
# A `workflow_run` goes on only when every one of these holds:
#   * the CI run concluded `success` on a branch named `main`;
#   * a `push` in this repository started it, because a fork's branch can also be called `main`;
#   * the tested commit is on `main` itself. A writer can push a TAG named `main`, and GitHub then puts
#     `main` in head_branch with event `push` from this repository. Only the commit's place in history
#     tells the two apart, so an unmerged commit never reaches build-and-push with ref `main`.
# A `workflow_dispatch` or a `v*` tag push passes through ungated, as before: the environments allow
# only `main`, so their deploy jobs run only for a dispatch from `main`.
#
# Env:     EVENT_NAME, FALLBACK_SHA (github.sha), GITHUB_REPOSITORY (all required); for a workflow_run
#          also WR_CONCLUSION, WR_BRANCH, WR_EVENT, WR_HEAD_REPO, WR_SHA, WR_ID, WR_URL; `gh` needs a
#          token with `contents: read` (GH_TOKEN in CI).
# Output:  proceed=<true|false>, deploy_sha=<sha>, short_sha=<7 chars> on stdout and in $GITHUB_OUTPUT
#          when set; the reason goes to $GITHUB_STEP_SUMMARY when set.
# Exit:    0 = a verdict was written (a refusal included), 2 = usage error.
#
# CI-only (Linux runners), so it has no .ps1 twin.
set -euo pipefail
export LC_ALL=C

for name in EVENT_NAME FALLBACK_SHA GITHUB_REPOSITORY; do
    if [ -z "${!name:-}" ]; then
        echo "::error::cd-gate.sh: ${name} is not set." >&2
        exit 2
    fi
done

summary() {
    cat >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
}

is_full_sha() {
    [[ "$1" =~ ^[0-9a-f]{40}$ ]]
}

# The tested commit is on main when main is the commit itself or a descendant of it. The branch is
# resolved through the branches API, which reads only branches, so a tag named `main` cannot answer.
# Any API failure counts as "not on main".
commit_is_on_main() {
    local main_sha status
    main_sha="$(gh api "repos/${GITHUB_REPOSITORY}/branches/main" --jq '.commit.sha')" || return 1
    is_full_sha "$main_sha" || return 1
    status="$(gh api "repos/${GITHUB_REPOSITORY}/compare/${1}...${main_sha}" --jq '.status')" || return 1
    [ "$status" = "identical" ] || [ "$status" = "ahead" ]
}

proceed=false
deploy_sha="$FALLBACK_SHA"

if [ "$EVENT_NAME" = "workflow_run" ]; then
    deploy_sha="${WR_SHA:-}"
    reason=""
    if [ "${WR_CONCLUSION:-}" != "success" ]; then
        reason="CI concluded **${WR_CONCLUSION:-unknown}**."
    elif [ "${WR_BRANCH:-}" != "main" ]; then
        reason="CI ran on **${WR_BRANCH:-unknown}**, not on main."
    elif [ "${WR_EVENT:-}" != "push" ]; then
        reason="A **${WR_EVENT:-unknown}** started the CI run, not a push."
    elif [ "${WR_HEAD_REPO:-}" != "$GITHUB_REPOSITORY" ]; then
        reason="The CI run came from **${WR_HEAD_REPO:-unknown}**, not from this repository."
    elif ! is_full_sha "$deploy_sha"; then
        reason="The tested commit \`${deploy_sha:-<empty>}\` is not a full commit SHA."
    elif ! commit_is_on_main "$deploy_sha"; then
        reason="The tested commit \`${deploy_sha}\` is not on main (a tag can also be called main), or GitHub could not confirm it."
    else
        proceed=true
    fi

    if [ "$proceed" = "true" ]; then
        summary <<EOF
### ✅ CI gate passed — deploy candidate

- CI run [#${WR_ID:-?}](${WR_URL:-}) concluded **${WR_CONCLUSION}** on **${WR_BRANCH}**
- Deploying commit \`${deploy_sha}\` — the exact commit CI tested, and it is on main
- Rollout still pauses at the \`production\` approval gate below.
EOF
    else
        # A refused run still needs a well-formed sha for the outputs below; nothing builds from it.
        is_full_sha "$deploy_sha" || deploy_sha="$FALLBACK_SHA"
        summary <<EOF
### ⏭️ Deploy skipped — no green CI run for a commit pushed to main in this repository

- CI run [#${WR_ID:-?}](${WR_URL:-}) on **${WR_BRANCH:-unknown}**, started by **${WR_EVENT:-unknown}** in **${WR_HEAD_REPO:-unknown}**
- ${reason}
- No image is built and no rollout is attempted.
EOF
    fi
else
    # workflow_dispatch (deploy on demand) or a v* tag push (publish artifacts only).
    proceed=true
    summary <<EOF
### ▶️ ${EVENT_NAME} — not CI-gated

- Commit \`${deploy_sha}\`
- Manual dispatch / tag publish. Only a run from \`main\` may deploy, and production still needs its approval.
EOF
fi

short_sha="$(printf '%s' "$deploy_sha" | cut -c1-7)"
{
    printf 'proceed=%s\n' "$proceed"
    printf 'deploy_sha=%s\n' "$deploy_sha"
    printf 'short_sha=%s\n' "$short_sha"
} | tee -a "${GITHUB_OUTPUT:-/dev/null}"
