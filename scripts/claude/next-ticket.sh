#!/usr/bin/env bash
# Deterministic READY-ticket picker for the claude backlog loop — no LLM, no tokens.
# Prints the number of the next ready ticket and exits 0; exits 1 when none is ready.
#
# "Ready" = open, not skip-labeled, not an [Epic]/[Tracking] title, written only by trusted
# maintainers (issue-trust.sh, when the settings turn the provenance gate on), no open PR and no
# `.claude/worktrees/ticket-<n>` yet (the PR waits for the owner's review, the worktree means a
# session is on it) unless that worktree was kept for a resume after a usage limit (work-ticket.sh
# then decides), and every "Depends on #X" reference in the body points at a CLOSED issue (a ticket
# is closed by its merged PR, so closed == merged). Order: priority-critical > priority-high >
# priority-medium > priority-low > no priority label, then issue number ascending.
#
# Usage: next-ticket.sh [--exclude "<n> <n>"]     # numbers already attempted this run
# Env:   LOOP_SKIP_LABELS   comma-separated labels that exclude a ticket, for this run (default:
#                           skip_labels in scripts/claude-loop.conf). loop-blocked is always
#                           skipped: the loop owns that label.
#        LOOP_SKIP_TITLES   jq regex over titles to exclude, for this run (default: skip_titles)
#        LOOP_SKIP_ISSUES   space-separated numbers to exclude, for this run (default: skip_issues)
#        see issue-trust.sh for the provenance knobs (LOOP_TRUSTED_ASSOCIATIONS / _LOGINS,
#        LOOP_TRUST_GATE)
set -euo pipefail

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here.
SCRIPT_DIR="$(CDPATH='' cd -- "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR/../.."
# shellcheck source=loop-config.sh
. "$SCRIPT_DIR/loop-config.sh"
load_loop_config "$PWD" || exit 1
# Worktrees live under the MAIN checkout, even when this runs from a worktree (see work-ticket.sh).
WORKTREES="$(dirname "$(git rev-parse --path-format=absolute --git-common-dir)")/.claude/worktrees"

EXCLUDE=""
if [ "${1:-}" = "--exclude" ]; then
    EXCLUDE="${2:-}"
fi
EXCLUDE="$EXCLUDE ${LOOP_SKIP_ISSUES-$LOOP_CFG_skip_issues}"

# The loop owns loop-blocked: a ticket that carries it waits for the owner's answer, whatever the
# settings list.
SKIP_LABELS="loop-blocked,${LOOP_SKIP_LABELS:-$LOOP_CFG_skip_labels}"
SKIP_TITLES="${LOOP_SKIP_TITLES-$LOOP_CFG_skip_titles}"

# type -P finds programs only: `gh` is also a function of loop-config.sh.
for tool in gh jq; do
    type -P "$tool" >/dev/null 2>&1 || { echo "next-ticket: missing dependency: $tool" >&2; exit 1; }
done

candidates="$(gh issue list --state open --limit 200 --json number,title,labels \
    | jq -r --arg skip "$SKIP_LABELS" --arg skipTitles "$SKIP_TITLES" '
        ($skip | split(",")) as $s
        | map({number, title, labels: [.labels[].name]})
        | map(select([.labels[] | IN($s[])] | any | not))
        | map(select(.title | test("^\\[(Epic|Tracking)\\]"; "i") | not))
        | map(select(if $skipTitles == "" then true else (.title | test($skipTitles) | not) end))
        | map(. + {prio: (
            if   (.labels | index("priority-critical")) then 0
            elif (.labels | index("priority-high"))     then 1
            elif (.labels | index("priority-medium"))   then 2
            elif (.labels | index("priority-low"))      then 3
            else 4 end)})
        | sort_by(.prio, .number)
        | .[].number')"

# The loop stops at the PR, so a worked ticket stays open until the owner merges it. Its branch
# is named "<n>-<title>" (gh issue develop). Fail closed: without this list every ticket that
# already has a PR would look ready again. A fork's PR is not ours, whatever its branch is called.
open_heads="$(gh pr list --state open --limit 200 --json headRefName,isCrossRepository \
    --jq '.[] | select(.isCrossRepository | not) | .headRefName')" || {
    echo "next-ticket: cannot list open pull requests (GitHub API) — refusing to pick" >&2
    exit 1
}

# A worktree the loop kept after a usage limit carries a marker in its own git folder (written by
# work-ticket.sh, which also checks it). A folder that is not a worktree of its own resolves to the
# main .git, so it never counts.
kept_for_resume() {
    local worktree="$WORKTREES/ticket-$1" git_dir
    [ -d "$worktree" ] || return 1
    git_dir="$(git -C "$worktree" rev-parse --path-format=absolute --git-dir 2>/dev/null)" || return 1
    [ "$git_dir" != "$(git -C "$worktree" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)" ] || return 1
    [ -s "$git_dir/loop-resume" ]
}

for n in $candidates; do
    case " $EXCLUDE " in
        *" $n "*) continue ;;
    esac
    # Its session may have opened the PR before the limit hit; work-ticket.sh tells that apart.
    if ! kept_for_resume "$n"; then
        if grep -q "^$n-" <<< "$open_heads"; then
            continue
        fi
        if [ -e "$WORKTREES/ticket-$n" ]; then
            continue
        fi
    fi

    # Provenance gate: on a public repo anyone can write the text the worker reads as
    # its task. An issue whose author — or any of whose commenters — lacks write access is never
    # ready. work-ticket.sh re-checks this, so naming a ticket explicitly cannot bypass it.
    if [ "$LOOP_CFG_provenance_gate" = "on" ]; then
        trust_rc=0
        "$SCRIPT_DIR/issue-trust.sh" "$n" || trust_rc=$?
        if [ "$trust_rc" -eq 2 ]; then
            # Say so loudly: an unreachable API skipping every candidate must not read as "backlog drained".
            echo "next-ticket: WARNING — could not verify #$n (GitHub API); skipping it (fail-closed)" >&2
        fi
        [ "$trust_rc" -eq 0 ] || continue
    fi

    # Dependency gate: every "Depends on #X" must be CLOSED (i.e. merged).
    deps="$(gh issue view "$n" --json body --jq '.body // ""' \
        | grep -iE 'depends[ -]on' | grep -oE '#[0-9]+' | tr -d '#' || true)"

    ready=1
    for d in $deps; do
        state="$(gh issue view "$d" --json state --jq .state 2>/dev/null || echo "UNKNOWN")"
        if [ "$state" = "OPEN" ]; then
            ready=0
            break
        fi
    done

    if [ "$ready" = "1" ]; then
        echo "$n"
        exit 0
    fi
done

exit 1
