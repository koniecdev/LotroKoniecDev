#!/usr/bin/env bash
#
# Lists what a release of <commit> would contain and proposes its number (#1440). The CD job that
# keeps the "Next release" issue up to date (scripts/ci/next-release-issue.sh) and the release script
# (scripts/release.sh) both call it, so the two always agree.
#
# The changes are the first-parent commits between the newest vX.Y.Z tag and <commit>. Each one is a
# squash-merged PR, and the PR's closing issues in this repository give the labels. A dependabot PR
# can close issues in another repository, and those never count.
#
# The number follows the wiki page Wersje-systemu:
#   * a server issue with type-feature → the next minor (v0.2.1 → v0.3.0);
#   * otherwise a server issue with type-bug → the next patch (v0.2.1 → v0.2.2);
#   * otherwise nothing is proposed.
# An issue whose title starts with `M4-` is a desktop-app ticket. It goes into its own group and
# never raises the number, because desktop releases have their own numbers (owner decision of
# 2026-11-22 on #1440). Nothing here ever proposes 1.0.0: that is the owner's call (epic #1346).
#
# A PR whose closing issues cannot be read is retried once. After that it is listed in its own group
# with a warning and never raises the number. It must not stop the issue job, or the signal would
# die on every deploy until the next tag (#1440).
#
# Usage:   next-release.sh --out-dir <dir> <commit>
# Env:     GITHUB_REPOSITORY (owner/name); `gh` signed in with read access (GH_TOKEN in CI).
# Writes:  <dir>/proposal       key=value lines, also printed on stdout:
#                               commit, base_tag, bump (minor | patch | none), version (vX.Y.Z or
#                               empty), changes, features, fixes, desktop, other, no_issue, unreadable
#          <dir>/changes.md     the grouped change list in Markdown (issue body, raw release notes)
#          <dir>/notes-input.md the same list with each issue's Problem and Fix, for the notes call
# Exit:    0 = done, 2 = usage error or a commit that does not exist, 3 = GitHub could not be read.
#
# Linux runners and Git Bash on Windows (release.sh runs it there), so it has no .ps1 twin.
set -euo pipefail
export LC_ALL=C

DESKTOP_GROUP='Desktop app (released on its own as desktop-vX.Y.Z)'
VERSION_PATTERN='^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
# Fields of one issue are split by the unit separator: `read` folds a run of tabs into one, so an
# issue with no labels would shift its body into the labels field.
US=$'\037'

usage() {
    echo "usage: next-release.sh --out-dir <dir> <commit>" >&2
    exit 2
}

# Prints the body of the `## <heading>` section of the Markdown on stdin, without the blank lines
# around it. Empty when the section is missing.
section() {
    awk -v heading="$1" '
        { sub(/\r$/, "") }
        /^## / {
            if (inside) { exit }
            if ($0 ~ ("^## +" heading "([^A-Za-z].*)?$")) { inside = 1; next }
        }
        inside { lines[++count] = $0 }
        END {
            first = 1
            while (first <= count && lines[first] ~ /^[[:space:]]*$/) { first++ }
            last = count
            while (last >= first && lines[last] ~ /^[[:space:]]*$/) { last-- }
            for (i = first; i <= last; i++) { print lines[i] }
        }
    '
}

# Runs `gh "$@"` and, when it fails, once more. Prints its stdout; returns its exit code. Only the
# second try shows gh's error, so a blip that the retry fixes leaves no noise in the log.
gh_read() {
    local output
    if output="$(gh "$@" 2>/dev/null)"; then
        printf '%s' "$output"
        return 0
    fi
    output="$(gh "$@")" || return $?
    printf '%s' "$output"
}

out_dir=""
commit=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --out-dir)
            [ "$#" -ge 2 ] || usage
            out_dir="$2"
            shift 2
            ;;
        -*) usage ;;
        *)
            [ -z "$commit" ] || usage
            commit="$1"
            shift
            ;;
    esac
done
[ -n "$out_dir" ] && [ -n "$commit" ] || usage

if [ -z "${GITHUB_REPOSITORY:-}" ]; then
    echo "next-release.sh: GITHUB_REPOSITORY is not set." >&2
    exit 2
fi
repo_lower="$(printf '%s' "$GITHUB_REPOSITORY" | tr '[:upper:]' '[:lower:]')"
owner="${GITHUB_REPOSITORY%%/*}"
name="${GITHUB_REPOSITORY#*/}"

if ! sha="$(git rev-parse --verify --quiet "${commit}^{commit}")"; then
    echo "next-release.sh: '${commit}' is not a commit in this clone." >&2
    exit 2
fi
mkdir -p "$out_dir" || exit 2

base_tag=""
tags="$(git tag --list 'v*' --sort=-v:refname)"
while IFS= read -r tag; do
    if [[ "$tag" =~ $VERSION_PATTERN ]]; then
        base_tag="$tag"
        break
    fi
done <<< "$tags"

major=0
minor=0
patch=0
if [ -n "$base_tag" ]; then
    [[ "$base_tag" =~ $VERSION_PATTERN ]]
    major="${BASH_REMATCH[1]}"
    minor="${BASH_REMATCH[2]}"
    patch="${BASH_REMATCH[3]}"
    base_sha="$(git rev-parse "${base_tag}^{commit}")"
    commits="$(git rev-list --first-parent --reverse "${base_sha}..${sha}")"
else
    commits="$(git rev-list --first-parent --reverse "$sha")"
fi

# shellcheck disable=SC2016 # GraphQL variables, not shell
query='query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { pullRequest(number: $number) { closingIssuesReferences(first: 100) { nodes { number title body repository { nameWithOwner } labels(first: 50) { nodes { name } } } } } } }'
issue_filter='.data.repository.pullRequest.closingIssuesReferences.nodes[] | select((.repository.nameWithOwner | ascii_downcase) == "'"$repo_lower"'") | [(.number | tostring), (.title | gsub("[\t\r\n]"; " ")), ([.labels.nodes[].name] | join(",")), ((.body // "") | @base64)] | join("\u001f")'

features_md=""
fixes_md=""
desktop_md=""
other_md=""
no_issue_md=""
unreadable_md=""
features_in=""
fixes_in=""
desktop_in=""
other_in=""
no_issue_in=""
unreadable_in=""
count_changes=0
count_features=0
count_fixes=0
count_desktop=0
count_other=0
count_no_issue=0
count_unreadable=0

# One closing issue as notes input: its number, type labels, title, Problem and Fix.
issue_input() {
    local number="$1" title="$2" labels="$3" body="$4" types problem fix
    types="$(tr ',' '\n' <<< "$labels" | grep -E '^type-' | paste -sd ',' - || true)"
    problem="$(section 'Problem' <<< "$body")"
    fix="$(section 'Fix' <<< "$body")"
    printf 'Closes #%s (%s): %s\n' "$number" "${types:-no type label}" "$title"
    if [ -n "$problem" ]; then
        printf '\nProblem:\n%s\n' "$problem"
    fi
    if [ -n "$fix" ]; then
        printf '\nFix:\n%s\n' "$fix"
    fi
}

while IFS= read -r c; do
    [ -n "$c" ] || continue
    count_changes=$((count_changes + 1))
    if ! pr="$(gh_read api "repos/${GITHUB_REPOSITORY}/commits/${c}/pulls" \
        --jq '(map(select(.merge_commit_sha == "'"$c"'")) + map(select(.merged_at != null))) | first // empty | "\(.number)\t\(.title | gsub("[\t\r\n]"; " "))"')"; then
        echo "next-release.sh: the pull request of ${c} could not be read." >&2
        exit 3
    fi
    pr="${pr//$'\r'/}"
    if [ -z "$pr" ]; then
        subject="$(git log -1 --format=%s "$c")"
        short="$(git rev-parse --short=7 "$c")"
        no_issue_md+="- ${short} ${subject} (no pull request)"$'\n'
        no_issue_in+="### Commit ${short}: ${subject}"$'\n\n'
        count_no_issue=$((count_no_issue + 1))
        continue
    fi
    pr_number="${pr%%$'\t'*}"
    pr_title="${pr#*$'\t'}"

    if ! issues="$(gh_read api graphql -f query="$query" -f owner="$owner" -f name="$name" \
        -F number="$pr_number" --jq "$issue_filter")"; then
        echo "::warning::next-release.sh: the closing issues of #${pr_number} could not be read, so it never raises the number." >&2
        unreadable_md+="- #${pr_number} ${pr_title}"$'\n'
        unreadable_in+="### PR #${pr_number}: ${pr_title}"$'\n\n'
        count_unreadable=$((count_unreadable + 1))
        continue
    fi
    issues="${issues//$'\r'/}"

    server_refs=""
    desktop_refs=""
    server_input=""
    desktop_input=""
    has_feature=0
    has_bug=0
    has_server=0
    while IFS="$US" read -r number title labels body64; do
        [ -n "$number" ] || continue
        body="$(base64 -d <<< "$body64" 2>/dev/null || true)"
        entry="$(issue_input "$number" "$title" "$labels" "$body")"
        if [[ "$title" == M4-* ]]; then
            desktop_refs+="${desktop_refs:+, }#${number}"
            desktop_input+="${entry}"$'\n\n'
            continue
        fi
        has_server=1
        server_refs+="${server_refs:+, }#${number}"
        server_input+="${entry}"$'\n\n'
        case ",${labels}," in
            *,type-feature,*) has_feature=1 ;;
        esac
        case ",${labels}," in
            *,type-bug,*) has_bug=1 ;;
        esac
    done <<< "$issues"

    heading="### PR #${pr_number}: ${pr_title}"$'\n'
    if [ "$has_feature" -eq 1 ]; then
        features_md+="- #${pr_number} ${pr_title} (closes ${server_refs})"$'\n'
        features_in+="${heading}${server_input}"
        count_features=$((count_features + 1))
    elif [ "$has_bug" -eq 1 ]; then
        fixes_md+="- #${pr_number} ${pr_title} (closes ${server_refs})"$'\n'
        fixes_in+="${heading}${server_input}"
        count_fixes=$((count_fixes + 1))
    elif [ "$has_server" -eq 1 ]; then
        other_md+="- #${pr_number} ${pr_title} (closes ${server_refs})"$'\n'
        other_in+="${heading}${server_input}"
        count_other=$((count_other + 1))
    elif [ -z "$desktop_refs" ]; then
        no_issue_md+="- #${pr_number} ${pr_title}"$'\n'
        no_issue_in+="${heading}"$'\n'
        count_no_issue=$((count_no_issue + 1))
    fi
    if [ -n "$desktop_refs" ]; then
        desktop_md+="- #${pr_number} ${pr_title} (closes ${desktop_refs})"$'\n'
        desktop_in+="${heading}${desktop_input}"
        count_desktop=$((count_desktop + 1))
    fi
done <<< "$commits"

if [ "$count_features" -gt 0 ]; then
    bump='minor'
    version="v${major}.$((minor + 1)).0"
elif [ "$count_fixes" -gt 0 ]; then
    bump='patch'
    version="v${major}.${minor}.$((patch + 1))"
else
    bump='none'
    version=""
fi

write_group() {
    if [ -n "$3" ]; then
        printf '%s %s\n\n%s\n' "$1" "$2" "$3"
    fi
}

{
    if [ "$count_changes" -eq 0 ]; then
        printf 'No changes since %s.\n' "${base_tag:-the start of the history}"
    fi
    write_group '###' 'Features' "$features_md"
    write_group '###' 'Fixes' "$fixes_md"
    write_group '###' "$DESKTOP_GROUP" "$desktop_md"
    write_group '###' 'Other' "$other_md"
    write_group '###' 'No closing issue' "$no_issue_md"
    write_group '###' 'Closing issues could not be read' "$unreadable_md"
} > "$out_dir/changes.md"

{
    write_group '##' 'Features' "$features_in"
    write_group '##' 'Fixes' "$fixes_in"
    write_group '##' "$DESKTOP_GROUP" "$desktop_in"
    write_group '##' 'Other' "$other_in"
    write_group '##' 'No closing issue' "$no_issue_in"
    write_group '##' 'Closing issues could not be read' "$unreadable_in"
} > "$out_dir/notes-input.md"

proposal="$(printf '%s\n' \
    "commit=${sha}" \
    "base_tag=${base_tag}" \
    "bump=${bump}" \
    "version=${version}" \
    "changes=${count_changes}" \
    "features=${count_features}" \
    "fixes=${count_fixes}" \
    "desktop=${count_desktop}" \
    "other=${count_other}" \
    "no_issue=${count_no_issue}" \
    "unreadable=${count_unreadable}")"
printf '%s\n' "$proposal" > "$out_dir/proposal"
printf '%s\n' "$proposal"
