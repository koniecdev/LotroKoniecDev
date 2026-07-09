#!/usr/bin/env bash
# Issue-provenance gate for the autonomous backlog loop (AUDIT-SEC-08 / ADR-0026).
#
# This repository is PUBLIC: anyone on GitHub can open an issue or comment on one. The loop feeds
# an issue to `/work-ticket <n>`, which reads its title, body AND comments as instructions, then
# auto-squash-merges the resulting PR into `main` once pr-verify is green. Untrusted issue text
# reaching that agent is therefore a prompt-injection channel straight to `main`, so every path
# that hands an issue to the worker asks this script first.
#
# Trusted == the writer's GitHub `author_association` is in $LOOP_TRUSTED_ASSOCIATIONS
# (OWNER / MEMBER / COLLABORATOR — the associations that carry write access), or the writer's
# login is in $LOOP_TRUSTED_LOGINS. It is evaluated for the issue author AND for every comment
# author, because "later comments override the body" is part of the worker's contract — gating
# only the author would leave the comment channel wide open on a maintainer-authored ticket.
#
# FAIL-CLOSED: an API error, a missing association, or an unknown association refuses the ticket.
#
# Usage: issue-trust.sh <issue-number>
# Exit:  0 trusted · 1 untrusted writer · 2 error (bad input / missing dependency / API failure)
#        Callers MUST refuse the ticket on any non-zero exit. All diagnostics go to stderr;
#        stdout stays empty so callers can keep their own stdout contract.
# Env:
#   LOOP_TRUSTED_ASSOCIATIONS  comma-separated allowlist (default: OWNER,MEMBER,COLLABORATOR)
#   LOOP_TRUSTED_LOGINS        comma-separated extra logins, e.g. a second maintainer account or
#                              a bot that comments on tickets (default: empty)
#   LOOP_TRUST_GATE=0          human escape hatch: accept the ticket unchecked for this run. Use it
#                              only after reading the issue AND its comments yourself.
set -euo pipefail

ISSUE="${1:?usage: issue-trust.sh <issue-number>}"

# Shape-check the argument BEFORE the escape hatch: a cheap guard that a human's env var can
# switch off is not a guard at all.
case "$ISSUE" in
    ''|*[!0-9]*) echo "issue-trust: not an issue number: '$ISSUE'" >&2; exit 2 ;;
esac

if [ "${LOOP_TRUST_GATE:-1}" = "0" ]; then
    echo "issue-trust: GATE DISABLED (LOOP_TRUST_GATE=0) — #$ISSUE accepted unchecked" >&2
    exit 0
fi

for tool in gh jq; do
    command -v "$tool" >/dev/null 2>&1 || { echo "issue-trust: missing dependency: $tool" >&2; exit 2; }
done

# Resolve the repository from the checkout this script lives in, never from the caller's cwd —
# `gh api repos/{owner}/{repo}` otherwise picks up whatever remote the current directory happens
# to have, and a security gate must not depend on where it was invoked from.
cd "$(cd "$(dirname "$0")/../.." && pwd)"

# Normalize both allowlists once: strip whitespace, upper-case the associations (GitHub returns
# them upper-case), and drop empty entries so a trailing comma can never match an empty value.
normalize_list() {
    printf '%s' "$1" | tr -d '[:space:]' | tr ',' '\n' | grep -v '^$' | paste -sd, - || true
}

TRUSTED_ASSOCIATIONS="$(normalize_list "$(printf '%s' "${LOOP_TRUSTED_ASSOCIATIONS:-OWNER,MEMBER,COLLABORATOR}" | tr '[:lower:]' '[:upper:]')")"
TRUSTED_LOGINS="$(normalize_list "${LOOP_TRUSTED_LOGINS:-}")"

in_list() {
    # $1 needle, $2 comma-separated haystack. An empty needle never matches.
    [ -n "$1" ] || return 1
    case ",$2," in
        *",$1,"*) return 0 ;;
        *) return 1 ;;
    esac
}

# Split one `[login, association] | @tsv` row. Done with parameter expansion rather than
# `IFS=$'\t' read`, which — tab being IFS whitespace — silently collapses a leading empty login
# and shifts the association into it.
row_login() { printf '%s' "${1%%$'\t'*}"; }
row_association() { printf '%s' "${1#*$'\t'}"; }

# $1 login, $2 author_association, $3 role (for the refusal message)
check_writer() {
    local login="$1" association="$2" role="$3"
    if [ -z "$login" ]; then
        echo "issue-trust: REFUSED #$ISSUE — $role has no identifiable author (deleted/ghost account)" >&2
        return 1
    fi
    in_list "$login" "$TRUSTED_LOGINS" && return 0
    in_list "$(printf '%s' "$association" | tr '[:lower:]' '[:upper:]')" "$TRUSTED_ASSOCIATIONS" && return 0
    echo "issue-trust: REFUSED #$ISSUE — $role by '$login' has author_association" \
         "'${association:-<none>}'; trusted: $TRUSTED_ASSOCIATIONS${TRUSTED_LOGINS:+ + logins $TRUSTED_LOGINS}" >&2
    return 1
}

api_failed() {
    echo "issue-trust: REFUSED #$ISSUE — cannot read $1 from the GitHub API; refusing (fail-closed)" >&2
    exit 2
}

issue_writer="$(gh api "repos/{owner}/{repo}/issues/$ISSUE" \
    --jq '[.user.login // "", .author_association // ""] | @tsv' 2>/dev/null)" \
    || api_failed "issue #$ISSUE"

issue_login="$(row_login "$issue_writer")"
issue_association="$(row_association "$issue_writer")"
check_writer "$issue_login" "$issue_association" "issue" || exit 1

# --paginate is load-bearing: without it only the first page of comments is checked, and a
# hostile comment posted after 30 benign ones would never be seen. The self-test pins it.
comment_writers="$(gh api "repos/{owner}/{repo}/issues/$ISSUE/comments" --paginate \
    --jq '.[] | [.user.login // "", .author_association // ""] | @tsv' 2>/dev/null)" \
    || api_failed "the comments of issue #$ISSUE"

comments=0
while IFS= read -r row; do
    [ -n "$row" ] || continue
    comments=$((comments + 1))
    check_writer "$(row_login "$row")" "$(row_association "$row")" "comment #$comments" || exit 1
done <<< "$comment_writers"

echo "issue-trust: #$ISSUE trusted — issue by $issue_login ($issue_association), $comments comment(s), all writers trusted" >&2
exit 0
