#!/usr/bin/env bash
#
# Cuts a server release vX.Y.Z from the tip of main, in a terminal, with no Claude session (#1440).
# Almost all of a release is waiting, and only the owner decides when to release. Starting this
# script is the owner's approval of the one prod deploy it needs; nothing is published before the
# owner's explicit yes at the last prompt.
#
#   1. Checks git and gh (signed in with a login that may approve `production`), then fetches.
#   2. Picks the release commit: the newest commit on main since the newest vX.Y.Z tag that has a CI
#      run. A commit with a gitleaks run but no CI run changed only paths CI ignores (docs), so it has
#      no images and is never deployed. It waits for that CI run; anything but success stops here.
#   3. Builds the change list (scripts/ci/next-release.sh) and starts the notes call in the
#      background, so the notes are written while the deploy runs.
#   4. Deploys the commit to prod, unless prod runs it already. It finds the commit's CD run by its
#      run name (cd.yml `run-name`): the run's own head_sha is main's tip when it started, not the
#      commit it deploys. A waiting prod job holds the `prod-mutation` group, so every other run
#      waiting at the gate is rejected first, and then the release commit's run is approved. A run
#      whose prod job never started (rejected, cancelled by the CD janitor or by a newer candidate)
#      is run again in full, never by a hand dispatch, which would skip the N-1 gate.
#   5. Proposes the number (next-release.sh), lets the owner type another one.
#   6. Takes the AI notes, or the raw change list when the call failed. AI never blocks a release.
#   7. Asks: Enter publishes, e edits the notes, v changes the version, q stops.
#   8. Tags, pushes the tag, publishes the GitHub Release and closes the "Next release" issue.
#
# Settings (environment):
#   RELEASE_POLL_SECONDS          seconds between two status checks, default 15
#   RELEASE_NOTES_GRACE_SECONDS   how long the prompt waits for the notes before it offers the raw
#                                 list, default 10
#   RELEASE_JQ, RELEASE_CLAUDE    the jq and claude commands, default jq and claude. jq only reads the
#                                 notes call's answer, so without it the AI notes are off, and the
#                                 release goes on with the raw list.
#
# Exit: 0 = released, nothing new, or stopped by the owner; 1 = stopped by a failed check, deploy or
#       publish; 2 = a missing tool or a bad argument.
#
# Bash only: it runs in Git Bash on Windows too (owner decision, 2026-11-22), so it has no .ps1 twin.
set -euo pipefail

POLL="${RELEASE_POLL_SECONDS:-15}"
NOTES_GRACE="${RELEASE_NOTES_GRACE_SECONDS:-10}"
JQ="${RELEASE_JQ:-jq}"
CLAUDE="${RELEASE_CLAUDE:-claude}"
MAX_RERUNS=2
PROD_JOB='Deploy to prod / Deploy to production (Hetzner)'
PROD_JOB_SKIPPED='Deploy to prod'
NEXT_RELEASE_JOB='Next release issue'
CI_WORKFLOW='.github/workflows/ci.yml'
GITLEAKS_WORKFLOW='.github/workflows/gitleaks.yml'
ADR_0070='docs/adr/0070-a-release-is-a-git-tag-and-cd-builds-each-commit-once.md'
VERSION_PATTERN='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
SKIP_MARKERS='\[(skip ci|ci skip|no ci|skip actions|actions skip)\]'
SCRIPT_DIR="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# Fields of one API row are split by the unit separator, never a tab: `read` folds a run of tabs into
# one, so an empty field (a run's conclusion while it runs) would shift the rest.
US=$'\037'

WORK=""
NOTES_PID=""
LAST_STATUS_LINE=""
REPO=""
LOGIN=""
R=""
CI_ID=""
CI_ATTEMPT=""
CI_CREATED=""
NEWEST_TAG=""
W=""
PROD_EVENT=""
PROD_TITLE=""
PROD_URL=""
AI_OFF=""

say() { printf '%s\n' "$*"; }

die() {
    printf 'release.sh: %s\n' "$*" >&2
    exit 1
}

die_tool() {
    printf 'release.sh: %s\n' "$*" >&2
    exit 2
}

# Prints a status line only when it differs from the last one, so a long wait is not a wall of text.
status() {
    if [ "$*" != "$LAST_STATUS_LINE" ]; then
        LAST_STATUS_LINE="$*"
        say "$*"
    fi
}

nap() {
    if [ "$POLL" != 0 ]; then
        sleep "$POLL"
    fi
}

cgrep() { LC_ALL=C grep "$@"; }

# gh with its output's CRs removed: on Windows some tools end lines with CRLF (#1236).
ghx() {
    local out
    out="$(gh "$@")" || return $?
    printf '%s' "${out//$'\r'/}"
}

stop_notes() {
    local winpid
    if [ -n "$NOTES_PID" ] && kill -0 "$NOTES_PID" 2>/dev/null; then
        case "$(uname -s)" in
            MINGW* | MSYS* | CYGWIN*)
                # Git Bash's kill ends only its own wrapper of a native program, not claude.exe.
                if winpid="$(cat "/proc/${NOTES_PID}/winpid" 2>/dev/null)"; then
                    taskkill //T //F //PID "$winpid" >/dev/null 2>&1 || true
                fi
                ;;
        esac
        kill "$NOTES_PID" 2>/dev/null || true
    fi
}

cleanup() {
    stop_notes
    if [ -n "$WORK" ]; then
        rm -rf "$WORK"
    fi
}

higher_of() {
    printf '%s\n%s\n' "$1" "$2" | sort -t . -k 1,1n -k 2,2n -k 3,3n | tail -n 1
}

# The runs of a push to main for commit $1: id, workflow path, status, conclusion, attempt, created
# time and URL, one run per line, split by $US.
commit_runs() {
    ghx api "repos/${REPO}/actions/runs?head_sha=$1&event=push&per_page=100" \
        --jq '.workflow_runs[] | [(.id | tostring), .path, .status, (.conclusion // ""), (.run_attempt | tostring), .created_at, .html_url] | join("\u001f")'
}

# The newest run of workflow $2 among the lines in $1.
pick_run() {
    awk -F "$US" -v path="$2" '$2 == path' <<< "$1" | sort -t "$US" -k 1,1n | tail -n 1
}

run_info() {
    ghx api "repos/${REPO}/actions/runs/$1" \
        --jq '[.status, (.conclusion // ""), .html_url, .display_title, .event] | join("\u001f")'
}

# The prod deploy job of run $1: status, conclusion, number of steps and URL. Empty when it is not
# listed yet. Its name is `Deploy to prod` while it is skipped, and longer once it started.
prod_job() {
    local jobs
    jobs="$(ghx api "repos/${REPO}/actions/runs/$1/jobs?per_page=100" \
        --jq '.jobs[] | select(.name == "'"$PROD_JOB"'" or .name == "'"$PROD_JOB_SKIPPED"'") | [.status, (.conclusion // ""), (.steps | length | tostring), .html_url] | join("\u001f")')" \
        || die "the jobs of CD run $1 could not be read."
    head -n 1 <<< "$jobs"
}

job_conclusion() {
    local out
    out="$(ghx api "repos/${REPO}/actions/runs/$1/jobs?per_page=100" \
        --jq '.jobs[] | select(.name == "'"$2"'") | (.conclusion // "")')" || return 0
    head -n 1 <<< "$out"
}

run_url() { printf 'https://github.com/%s/actions/runs/%s' "$REPO" "$1"; }

is_run_of_release() {
    [ "$1" = workflow_run ] && [[ "$2" == "CD for ${R} ("* ]]
}

# Sets PROD_EVENT, PROD_TITLE and PROD_URL from the newest production deployment that ever reached
# success. A newer success re-marks an older one `inactive`, so the whole status history counts.
read_prod() {
    local deployments id url info
    PROD_EVENT=""
    PROD_TITLE=""
    PROD_URL=""
    deployments="$(ghx api --paginate "repos/${REPO}/deployments?environment=production&per_page=100" \
        --jq '.[] | (.id | tostring)')" || die "the production deployments could not be read."
    while IFS= read -r id; do
        [ -n "$id" ] || continue
        url="$(ghx api "repos/${REPO}/deployments/${id}/statuses?per_page=100" \
            --jq '[.[] | select(.state == "success") | .target_url] | first // empty')" \
            || die "the statuses of deployment ${id} could not be read."
        [ -n "$url" ] || continue
        if [[ ! "$url" =~ /actions/runs/([0-9]+) ]]; then
            PROD_TITLE="deployment ${id}, which names no workflow run"
            return 0
        fi
        info="$(ghx api "repos/${REPO}/actions/runs/${BASH_REMATCH[1]}" \
            --jq '[.event, .display_title, .html_url] | join("\u001f")')" || die "the CD run of deployment ${id} could not be read."
        IFS="$US" read -r PROD_EVENT PROD_TITLE PROD_URL <<< "$info"
        return 0
    done <<< "$deployments"
    PROD_TITLE="nothing (no production deployment ever succeeded)"
}

prod_runs_release() {
    read_prod
    is_run_of_release "$PROD_EVENT" "$PROD_TITLE"
}

# Prints "<environment id>$US<can approve>" when run $1 waits at the production gate.
pending_production() {
    ghx api "repos/${REPO}/actions/runs/$1/pending_deployments" \
        --jq '.[] | select(.environment.name == "production") | [(.environment.id | tostring), (.current_user_can_approve | tostring)] | join("\u001f")'
}

still_waiting() {
    local info
    info="$(run_info "$1")" || return 1
    [ "${info%%"$US"*}" = waiting ]
}

# review <run id> <approved|rejected> <comment>: 0 = done, 1 = the run no longer waits.
review() {
    local id="$1" state="$2" note="$3" pending env_id can
    pending="$(pending_production "$id")" || die "the pending deployments of CD run ${id} could not be read."
    [ -n "$pending" ] || return 1
    env_id="${pending%%"$US"*}"
    can="${pending#*"$US"}"
    if [ "$can" != true ]; then
        die "your gh login (${LOGIN}) may not review the production deploy of $(run_url "$id"). Sign in with the owner's account."
    fi
    if ! gh api -X POST "repos/${REPO}/actions/runs/${id}/pending_deployments" \
        -F "environment_ids[]=${env_id}" -f "state=${state}" -f "comment=${note}" >/dev/null; then
        if still_waiting "$id"; then
            die "GitHub refused to mark $(run_url "$id") ${state}, and it still waits at the gate. See gh's error above."
        fi
        return 1
    fi
    return 0
}

# The gate, every poll: approve a waiting run of the release commit and reject every other one.
review_waiting_runs() {
    local waiting id event title note
    waiting="$(ghx api "repos/${REPO}/actions/workflows/cd.yml/runs?status=waiting&per_page=100" \
        --jq '.workflow_runs[] | [(.id | tostring), .event, .display_title] | join("\u001f")')" \
        || die "the waiting CD runs could not be read."
    while IFS="$US" read -r id event title; do
        [ -n "$id" ] || continue
        if is_run_of_release "$event" "$title"; then
            [ -z "$W" ] || continue
            if [ "$PROD_EVENT" = workflow_dispatch ]; then
                say "WARNING: prod runs a hand run of CD: ${PROD_TITLE} (${PROD_URL}). Approving deploys ${R} over it."
            else
                say "Prod runs now: ${PROD_TITLE}${PROD_URL:+ (${PROD_URL})}."
            fi
            if review "$id" approved "Approved by scripts/release.sh to release ${R}."; then
                W="$id"
                say "Approved the prod deploy of $(run_url "$id")."
                say "From now on Ctrl-C stops only this script, not the deploy on GitHub."
            fi
        else
            note="Rejected by scripts/release.sh: the release of ${R} is being deployed. Re-run this run after the release if it is still needed."
            if review "$id" rejected "$note"; then
                say "Rejected $(run_url "$id") (${title}), so the release can go first."
            fi
        fi
    done <<< "$waiting"
}

find_candidate() {
    local title="CD for ${R} (CI run ${CI_ID}, attempt ${CI_ATTEMPT})" out
    out="$(ghx api "repos/${REPO}/actions/workflows/cd.yml/runs?created=>=${CI_CREATED}&per_page=100" \
        --jq '.workflow_runs[] | select(.display_title == "'"$title"'") | (.id | tostring)')" \
        || die "the CD runs could not be read."
    head -n 1 <<< "$out"
}

rerun() {
    local id="$1" why="$2"
    RERUNS=$((RERUNS + 1))
    if [ "$RERUNS" -gt "$MAX_RERUNS" ]; then
        die "the prod job of $(run_url "$id") ended without a deploy $((MAX_RERUNS + 1)) times. Look at the run, then start again."
    fi
    say "The prod job of $(run_url "$id") ended without starting (${why}). Running the whole run again (${RERUNS} of ${MAX_RERUNS})."
    say "Its staging deploy runs again too, so staging goes back to ${R} until the next merge."
    if ! gh api -X POST "repos/${REPO}/actions/runs/${id}/rerun" >/dev/null; then
        die "GitHub refused to run $(run_url "$id") again (a run older than 30 days cannot be re-run). Use the manual steps in the runbook, \"Releases\"."
    fi
    W=""
    LAST_STATUS_LINE=""
}

# Steps 4: wait, review the gate and follow the run until the release commit is on prod.
deploy_release() {
    local target info run_status job job_status conclusion steps
    RERUNS=0
    say "Waiting for the CD run of ${R} (CI run ${CI_ID}, attempt ${CI_ATTEMPT})."
    while true; do
        review_waiting_runs
        target="${W:-$(find_candidate)}"
        if [ -z "$target" ]; then
            status "  The CD run of ${R} has not started yet."
            nap
            continue
        fi
        info="$(run_info "$target")" || die "CD run ${target} could not be read."
        run_status="${info%%"$US"*}"
        job="$(prod_job "$target")"
        if [ -z "$job" ]; then
            if [ "$run_status" = completed ]; then
                die "$(run_url "$target") ended before its prod job (a red staging deploy or a refused gate). Nothing is tagged."
            fi
            status "  $(run_url "$target"): ${run_status}, before the prod job."
            nap
            continue
        fi
        IFS="$US" read -r job_status conclusion steps _ <<< "$job"
        case "$job_status" in
            completed) ;;
            in_progress)
                W="$target"
                status "  $(run_url "$target"): deploying to prod."
                nap
                continue
                ;;
            *)
                status "  $(run_url "$target"): its prod job is ${job_status}."
                nap
                continue
                ;;
        esac
        case "$conclusion" in
            success)
                W="$target"
                return 0
                ;;
            skipped)
                die "$(run_url "$target") skipped its prod job (a red staging deploy or a refused gate). Nothing is tagged."
                ;;
        esac
        if [ "$steps" != 0 ]; then
            die "the prod deploy of $(run_url "$target") ended '${conclusion}' (a red rollout or a rollback). Nothing is tagged."
        fi
        if [ "$run_status" != completed ]; then
            status "  $(run_url "$target"): its prod job ended without starting; waiting for the run to end."
            nap
            continue
        fi
        case "$conclusion" in
            failure) rerun "$target" "it was rejected" ;;
            cancelled) rerun "$target" "it was cancelled, by the CD janitor or by a newer candidate" ;;
            *) rerun "$target" "it ended '${conclusion}'" ;;
        esac
        nap
    done
}

# After the deploy: wait for the whole run (its issue job included), check prod, and reject other
# runs of the release commit that reach the gate later.
finish_deploy() {
    local info out id run_status title nr note
    while true; do
        info="$(run_info "$W")" || die "CD run ${W} could not be read."
        [ "${info%%"$US"*}" = completed ] && break
        status "  $(run_url "$W"): prod is done; waiting for the rest of the run."
        nap
    done
    nr="$(job_conclusion "$W" "$NEXT_RELEASE_JOB")"
    if [ "$nr" = failure ]; then
        say "WARNING: the \"${NEXT_RELEASE_JOB}\" job of $(run_url "$W") failed. The release goes on; this script closes the issue at the end."
    fi
    if ! prod_runs_release; then
        die "the deploy ended, but prod does not run ${R}: it runs ${PROD_TITLE}. Nothing is tagged."
    fi
    say "Prod runs ${R}."
    note="Rejected by scripts/release.sh: ${R} is already deployed by run ${W}."
    while true; do
        out="$(ghx api "repos/${REPO}/actions/workflows/cd.yml/runs?created=>=${CI_CREATED}&per_page=100" \
            --jq '.workflow_runs[] | select(.status != "completed" and .event == "workflow_run" and (.display_title | startswith("CD for '"$R"' ("))) | [(.id | tostring), .status, .display_title] | join("\u001f")')" \
            || die "the CD runs could not be read."
        out="$(awk -F "$US" -v w="$W" '$1 != w' <<< "$out")"
        [ -n "$out" ] || break
        while IFS="$US" read -r id run_status title; do
            [ -n "$id" ] || continue
            if [ "$run_status" = waiting ] && review "$id" rejected "$note"; then
                say "Rejected $(run_url "$id"): it would deploy ${R} again."
            fi
        done <<< "$out"
        status "  Waiting for other CD runs of ${R} to reach the gate."
        nap
    done
}

start_notes() {
    local input="$WORK/notes-prompt.txt"
    if [ -n "$AI_OFF" ]; then
        say "AI notes are off (${AI_OFF}): the raw change list will be used."
        return 0
    fi
    {
        cat "$SCRIPT_DIR/release-notes-prompt.txt"
        printf '\n---\n\n'
        printf 'Proposed version: %s\n' "${VERSION:-none yet, the owner picks one}"
        printf 'Previous release: %s\n' "${NEWEST_TAG:-none}"
        printf 'Server images: sha-%s\n\n' "$(git rev-parse --short=7 "$R")"
        cat "$WORK/changes/notes-input.md"
    } > "$input"
    mkdir -p "$WORK/notes-cwd"
    # An empty folder, so the call reads no repo CLAUDE.md; no tools and no MCP servers. Its stdin is
    # the file, so it never reads the owner's answers, and exec makes its PID the one to stop.
    (
        cd "$WORK/notes-cwd" || exit 2
        exec "$CLAUDE" -p --tools "" --strict-mcp-config --no-session-persistence --output-format json \
            < "$input" > "$WORK/notes.json" 2> "$WORK/notes.err"
    ) &
    NOTES_PID=$!
    say "Writing the release notes in the background."
}

raw_notes() {
    {
        printf '> The AI release notes failed (%s), so these notes are the raw change list.\n\n' "$1"
        cat "$WORK/changes/changes.md"
    } > "$WORK/notes.md"
    say "Using the raw change list: the AI notes failed (${1})."
}

collect_notes() {
    local waited=0 answer rc=0 result
    if [ -n "$AI_OFF" ]; then
        raw_notes "$AI_OFF"
        return 0
    fi
    while kill -0 "$NOTES_PID" 2>/dev/null && [ "$waited" -lt $((NOTES_GRACE * 5)) ]; do
        sleep 0.2
        waited=$((waited + 1))
    done
    if kill -0 "$NOTES_PID" 2>/dev/null; then
        say "The AI notes are still being written. Type r and Enter for the raw list, or wait."
        while kill -0 "$NOTES_PID" 2>/dev/null; do
            if IFS= read -r -t 1 answer; then
                if [ "$answer" = r ]; then
                    stop_notes
                    NOTES_PID=""
                    raw_notes "you chose the raw list"
                    return 0
                fi
            elif [ "$?" -le 128 ]; then
                # No more input will come (not a timeout), so only the notes can end the wait.
                sleep 1
            fi
        done
    fi
    wait "$NOTES_PID" || rc=$?
    NOTES_PID=""
    if [ "$rc" -ne 0 ]; then
        raw_notes "the claude call exited with ${rc}"
        return 0
    fi
    result="$("$JQ" -r 'if .is_error == true then empty else (.result // empty) end' "$WORK/notes.json" 2>/dev/null)" || result=""
    result="${result//$'\r'/}"
    if [ -z "$result" ]; then
        raw_notes "the claude call returned no notes: an error or a usage limit"
    elif ! cgrep -q '^## ' <<< "$result"; then
        raw_notes "the answer has no ## heading"
    else
        printf '%s\n' "$result" > "$WORK/notes.md"
    fi
}

# Prints the version without a leading v when $1 is X.Y.Z (or vX.Y.Z) above the newest tag.
check_version() {
    local typed="${1#v}"
    if [[ ! "$typed" =~ $VERSION_PATTERN ]]; then
        say "'$1' is not a version like 0.3.0." >&2
        return 1
    fi
    if [ -n "$NEWEST_TAG" ]; then
        if [ "${NEWEST_TAG#v}" = "$typed" ] || [ "$(higher_of "${NEWEST_TAG#v}" "$typed")" != "$typed" ]; then
            say "${typed} is not above the newest release, ${NEWEST_TAG}." >&2
            return 1
        fi
    fi
    printf '%s' "$typed"
}

edit_notes() {
    local editor
    if ! editor="$(git var GIT_EDITOR)"; then
        say "No editor is set (GIT_EDITOR, core.editor, VISUAL or EDITOR); the notes stay as they were."
        return 0
    fi
    cp "$WORK/notes.md" "$WORK/notes.edit.md"
    if sh -c "${editor} \"\$@\"" "$editor" "$WORK/notes.edit.md"; then
        mv "$WORK/notes.edit.md" "$WORK/notes.md"
    else
        say "The editor failed; the notes stay as they were."
    fi
}

publish() {
    local tag="v${VERSION}" url keep
    git tag -a "$tag" "$R" -m "$tag" || die "the tag ${tag} could not be made."
    git push origin "$tag" || die "the tag ${tag} could not be pushed. It exists here; push it with: git push origin ${tag}"
    if ! url="$(ghx release create "$tag" --verify-tag --latest --title "$tag" --notes-file "$WORK/notes.md")"; then
        keep="$(git rev-parse --absolute-git-dir)/release-notes-${tag}.md"
        cp "$WORK/notes.md" "$keep"
        die "the tag ${tag} is pushed, but the GitHub Release was not created. The tag stays. Finish with:
  gh release create ${tag} --verify-tag --latest --title \"${tag}\" --notes-file \"${keep}\""
    fi
    url="$(tail -n 1 <<< "$url")"
    say "Published ${tag}: ${url}"
    if ! "$SCRIPT_DIR/ci/next-release-issue.sh" close "$tag" "$url"; then
        say "WARNING: the \"Next release\" issue could not be closed. Close it by hand."
    fi
}

if [ "$#" -gt 0 ]; then
    echo "usage: scripts/release.sh   (no arguments; run it in a clone of the repo, in a terminal)" >&2
    exit 2
fi

trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# --- 1. Tools ------------------------------------------------------------------------------------
command -v git >/dev/null 2>&1 || die_tool "git is not installed."
command -v gh >/dev/null 2>&1 || die_tool "gh is not installed (https://cli.github.com)."
if ! repo_root="$(git rev-parse --show-toplevel 2>/dev/null)"; then
    die_tool "run it inside a clone of the repo."
fi
cd "$repo_root"
LOGIN="$(ghx api user --jq .login 2>/dev/null)" || die_tool "gh is not signed in. Run: gh auth login"
REPO="$(ghx repo view --json nameWithOwner --jq .nameWithOwner)" || die_tool "gh cannot tell which GitHub repository this clone is."
export GITHUB_REPOSITORY="$REPO"
if ! command -v "$JQ" >/dev/null 2>&1; then
    AI_OFF="jq is not installed"
elif ! command -v "$CLAUDE" >/dev/null 2>&1; then
    AI_OFF="the claude CLI is not installed"
fi
say "Releasing ${REPO} as ${LOGIN}."
git fetch --quiet --tags origin || die "git fetch failed."

# --- 2. The release commit -----------------------------------------------------------------------
tags="$(git tag --list 'v*' --sort=-v:refname)"
while IFS= read -r tag; do
    if [[ "${tag#v}" =~ $VERSION_PATTERN ]] && [[ "$tag" == v* ]]; then
        NEWEST_TAG="$tag"
        break
    fi
done <<< "$tags"
if [ -n "$NEWEST_TAG" ]; then
    candidates="$(git rev-list --first-parent "$(git rev-parse "${NEWEST_TAG}^{commit}")..origin/main")"
else
    candidates="$(git rev-list --first-parent origin/main)"
fi

while IFS= read -r c; do
    [ -n "$c" ] || continue
    reads=0
    while true; do
        runs="$(commit_runs "$c")" || die "the runs of ${c} could not be read."
        if [ -n "$(pick_run "$runs" "$CI_WORKFLOW")" ]; then
            R="$c"
            break 2
        fi
        if [ -n "$(pick_run "$runs" "$GITLEAKS_WORKFLOW")" ]; then
            if [ "$reads" -ge 1 ]; then
                say "$(git log -1 --format='%h %s' "$c"): no CI run (it changed only files CI ignores), so it was never deployed. Looking at the commit before it."
                break
            fi
            reads=1
            nap
            continue
        fi
        if cgrep -qiE "$SKIP_MARKERS" <<< "$(git log -1 --format=%B "$c")"; then
            say "$(git log -1 --format='%h %s' "$c"): its message skips GitHub Actions, so it has no runs. Looking at the commit before it."
            break
        fi
        status "$(git log -1 --format='%h %s' "$c"): no CI or gitleaks run yet, waiting for GitHub."
        nap
    done
done <<< "$candidates"

if [ -z "$R" ]; then
    say "Nothing new to release: no deployable commit on main since ${NEWEST_TAG:-the start of the history}."
    exit 0
fi
say "Release commit: $(git log -1 --format='%h %s' "$R")"
if ! git cat-file -e "${R}:${ADR_0070}" 2>/dev/null; then
    die "${R} does not contain ADR-0070, so it must not be tagged (runbook, \"Releases\")."
fi

while true; do
    runs="$(commit_runs "$R")" || die "the runs of ${R} could not be read."
    IFS="$US" read -r CI_ID _ ci_status ci_conclusion CI_ATTEMPT CI_CREATED ci_url <<< "$(pick_run "$runs" "$CI_WORKFLOW")"
    [ "$ci_status" = completed ] && break
    status "  CI of ${R}: ${ci_status} (${ci_url})."
    nap
done
if [ "$ci_conclusion" != success ]; then
    die "CI of ${R} ended '${ci_conclusion}': ${ci_url}. Nothing is deployed or tagged."
fi
say "CI of ${R} passed."

# --- 3. The change list and the notes ------------------------------------------------------------
WORK="$(mktemp -d)"
if ! proposal="$("$SCRIPT_DIR/ci/next-release.sh" --out-dir "$WORK/changes" "$R")"; then
    die "the change list could not be built."
fi
BUMP="$(sed -n 's/^bump=//p' <<< "$proposal")"
VERSION="$(sed -n 's/^version=v//p' <<< "$proposal")"
start_notes

# --- 4. Prod --------------------------------------------------------------------------------------
if prod_runs_release; then
    say "Prod already runs ${R}: no deploy needed."
else
    deploy_release
    finish_deploy
fi

# --- 5. The number --------------------------------------------------------------------------------
say ""
cat "$WORK/changes/changes.md"
say ""
if [ "$BUMP" = none ]; then
    say "No change raises the version number, so no release is proposed. Prod keeps the new deploy."
    while true; do
        printf 'Type a version (X.Y.Z) to release anyway, or press Enter to stop: '
        IFS= read -r answer || answer=""
        if [ -z "$answer" ]; then
            say "Stopped. Nothing was tagged."
            exit 0
        fi
        if typed="$(check_version "$answer")"; then
            VERSION="$typed"
            break
        fi
    done
else
    say "Proposed version: v${VERSION} (${BUMP}; newest release: ${NEWEST_TAG:-none})."
fi

# --- 6. The notes ---------------------------------------------------------------------------------
collect_notes

# --- 7. The owner's yes ---------------------------------------------------------------------------
while true; do
    say ""
    say "===== v${VERSION} on ${R} ====="
    cat "$WORK/notes.md"
    say "====="
    printf 'Enter = publish, e = edit the notes, v = change the version, q = stop: '
    if ! IFS= read -r answer; then
        say ""
        say "No answer: stopping. Nothing was tagged or published."
        exit 1
    fi
    case "$answer" in
        '') break ;;
        e) edit_notes ;;
        v)
            printf 'New version (X.Y.Z): '
            IFS= read -r answer || answer=""
            if typed="$(check_version "$answer")"; then
                VERSION="$typed"
            fi
            ;;
        q)
            say "Stopped. Nothing was tagged or published."
            exit 0
            ;;
        *) say "Unknown answer '${answer}'." ;;
    esac
done

# --- 8. Publish -----------------------------------------------------------------------------------
publish
