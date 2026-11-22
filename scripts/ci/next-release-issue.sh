#!/usr/bin/env bash
#
# Keeps one open "Next release" issue in step with what prod runs (#1440). The owner decides when to
# release; the issue only shows what is live but not released yet, and the proposed number.
#
#   next-release-issue.sh update <image tag>
#       Run by the next-release job of cd.yml after every successful prod deploy, with the image tag
#       that job rolled. It opens, edits or closes the issue. It comments only when the issue opens
#       or the proposed number goes from a patch to a minor, so the owner is told then and not on
#       every deploy. A hand run of CD can roll any tag: one that is not `sha-<7 hex>` of a commit on
#       main names no commit we can list, so it only warns and leaves the issue as it is.
#   next-release-issue.sh close <tag> <release url>
#       Run by scripts/release.sh after it published the release.
#
# The issue is found by a hidden marker in its body, among open issues the workflow's bot opened,
# so a title edit never makes a second one. The list is read and matched here, not searched: the
# search index can lag behind an issue opened a minute ago.
#
# Env:   GITHUB_REPOSITORY (owner/name); `gh` signed in (GH_TOKEN in CI). For update also REPO_OWNER
#        (the login the issue mentions) and optionally RUN_URL (the CD run, linked in the body).
#        ISSUE_AUTHOR is the login that opens the issue, default github-actions[bot].
# Exit:  0 = done (a skipped hand-run tag included), 2 = usage error, 3 = GitHub could not be read
#        or written.
#
# Linux runners and Git Bash on Windows (release.sh runs the close mode there), so no .ps1 twin.
set -euo pipefail
export LC_ALL=C

MARKER='<!-- lotro-next-release -->'
BUMP_MARKER_PREFIX='<!-- next-release-bump: '
SCRIPT_DIR="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
work=""
trap '[ -z "$work" ] || rm -rf "$work"' EXIT

usage() {
    echo "usage: next-release-issue.sh update <image tag> | close <tag> <release url>" >&2
    exit 2
}

# Sets $issue_number and $issue_bump from the open marked issue, or leaves both empty.
find_issue() {
    local author list first count
    author="${ISSUE_AUTHOR:-github-actions[bot]}"
    author="${author//\[/%5B}"
    author="${author//\]/%5D}"
    if ! list="$(gh api --paginate "repos/${GITHUB_REPOSITORY}/issues?state=open&creator=${author}&per_page=100" \
        --jq '.[] | select(.pull_request == null) | select((.body // "") | contains("'"$MARKER"'")) | "\(.number)\t\([(.body // "") | capture("<!-- next-release-bump: (?<b>[a-z]+) -->") | .b] | first // "")"')"; then
        echo "::error::next-release-issue.sh: the open issues could not be read." >&2
        exit 3
    fi
    list="${list//$'\r'/}"
    issue_number=""
    issue_bump=""
    [ -n "$list" ] || return 0
    list="$(sort -n <<< "$list")"
    count="$(grep -c . <<< "$list")"
    first="$(head -n 1 <<< "$list")"
    issue_number="${first%%$'\t'*}"
    issue_bump="${first#*$'\t'}"
    if [ "$count" -gt 1 ]; then
        echo "::warning::next-release-issue.sh: ${count} open issues carry the marker; updating the oldest, #${issue_number}. Close the others by hand." >&2
    fi
}

write_failed() {
    echo "::error::next-release-issue.sh: $1" >&2
    exit 3
}

comment() {
    gh api -X POST "repos/${GITHUB_REPOSITORY}/issues/$1/comments" -F "body=@$2" >/dev/null \
        || write_failed "the comment on #$1 could not be written."
}

close_issue() {
    gh api -X PATCH "repos/${GITHUB_REPOSITORY}/issues/$1" -f state=closed -f state_reason=completed >/dev/null \
        || write_failed "#$1 could not be closed."
}

update() {
    local tag="$1" short sha proposal bump version base_tag body title text
    if [[ ! "$tag" =~ ^sha-[0-9a-f]{7}$ ]]; then
        echo "::warning::next-release-issue.sh: prod now runs image '${tag}', which is not a sha-<7 hex> tag, so it names no commit to list. The \"Next release\" issue stays as it is."
        return 0
    fi
    short="${tag#sha-}"
    if ! sha="$(git rev-parse --verify --quiet "${short}^{commit}")" || ! git merge-base --is-ancestor "$sha" HEAD; then
        echo "::warning::next-release-issue.sh: '${tag}' is not one commit on main in this checkout, so the \"Next release\" issue stays as it is."
        return 0
    fi
    if [[ ! "${REPO_OWNER:-}" =~ ^[A-Za-z0-9-]+$ ]]; then
        echo "next-release-issue.sh: REPO_OWNER must be a GitHub login." >&2
        exit 2
    fi

    work="$(mktemp -d)"
    if ! proposal="$("$SCRIPT_DIR/next-release.sh" --out-dir "$work" "$sha")"; then
        echo "::error::next-release-issue.sh: the change list of ${sha} could not be built." >&2
        exit 3
    fi
    bump="$(sed -n 's/^bump=//p' <<< "$proposal")"
    version="$(sed -n 's/^version=//p' <<< "$proposal")"
    base_tag="$(sed -n 's/^base_tag=//p' <<< "$proposal")"

    find_issue

    if [ "$bump" = none ]; then
        if [ -n "$issue_number" ]; then
            printf 'No change live on prod since %s raises the version number, so no release is proposed. Closing.\n' \
                "${base_tag:-the start of the history}" > "$work/comment.md"
            comment "$issue_number" "$work/comment.md"
            close_issue "$issue_number"
            echo "Closed #${issue_number}: nothing live raises the number."
        else
            echo "Nothing live raises the number, so no \"Next release\" issue is needed."
        fi
        return 0
    fi

    title="Next release: ${version}"
    body="$work/body.md"
    {
        printf '%s\n%s%s -->\n\n' "$MARKER" "$BUMP_MARKER_PREFIX" "$bump"
        printf '@%s these changes run on prod but are not released yet.\n\n' "$REPO_OWNER"
        printf -- '- **Proposed version:** %s (newest release: %s)\n' "$version" "${base_tag:-none}"
        # shellcheck disable=SC2016 # Markdown backticks, not a command
        printf -- '- **Prod image:** `%s` (commit %s)\n' "$tag" "$sha"
        if [ -n "${RUN_URL:-}" ]; then
            printf -- '- **Updated by:** %s\n' "$RUN_URL"
        fi
        printf '\n'
        cat "$work/changes.md"
        # shellcheck disable=SC2016 # Markdown backticks, not a command
        printf '\nYou decide when to release: run `scripts/release.sh` in a terminal (runbook → "Releases"). Desktop-app changes are listed but never raise this number. This issue is edited after every prod deploy.\n'
    } > "$body"

    if [ -z "$issue_number" ]; then
        if ! issue_number="$(gh api -X POST "repos/${GITHUB_REPOSITORY}/issues" -f "title=${title}" \
            -F "body=@${body}" --jq '.number')"; then
            write_failed "the \"Next release\" issue could not be opened."
        fi
        issue_number="${issue_number//$'\r'/}"
        # shellcheck disable=SC2016 # Markdown backticks, not a command
        printf '@%s a release can be cut: %s is proposed. Run `scripts/release.sh` when you decide to release.\n' \
            "$REPO_OWNER" "$version" > "$work/comment.md"
        comment "$issue_number" "$work/comment.md"
        echo "Opened #${issue_number}: ${title}."
        return 0
    fi

    gh api -X PATCH "repos/${GITHUB_REPOSITORY}/issues/${issue_number}" -f "title=${title}" -F "body=@${body}" >/dev/null \
        || write_failed "#${issue_number} could not be edited."
    if [ "$issue_bump" = patch ] && [ "$bump" = minor ]; then
        text="@${REPO_OWNER} the proposed version went up to ${version}: a feature is now live."
        printf '%s\n' "$text" > "$work/comment.md"
        comment "$issue_number" "$work/comment.md"
    fi
    echo "Updated #${issue_number}: ${title}."
}

close_released() {
    local tag="$1" url="$2"
    find_issue
    if [ -z "$issue_number" ]; then
        echo "No open \"Next release\" issue to close."
        return 0
    fi
    work="$(mktemp -d)"
    printf 'Released as [%s](%s).\n' "$tag" "$url" > "$work/comment.md"
    comment "$issue_number" "$work/comment.md"
    close_issue "$issue_number"
    echo "Closed #${issue_number}."
}

if [ -z "${GITHUB_REPOSITORY:-}" ]; then
    echo "next-release-issue.sh: GITHUB_REPOSITORY is not set." >&2
    exit 2
fi

case "${1-}" in
    update)
        [ "$#" -eq 2 ] || usage
        update "$2"
        ;;
    close)
        [ "$#" -eq 3 ] || usage
        close_released "$2" "$3"
        ;;
    *) usage ;;
esac
