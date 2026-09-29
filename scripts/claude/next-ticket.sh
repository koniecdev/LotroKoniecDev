#!/usr/bin/env bash
# Deterministic READY-ticket picker for the claude backlog loop — no LLM, no tokens.
# Prints the number of the next ready ticket and exits 0; exits 1 when none is ready.
#
# "Ready" = open, not skip-labeled, not an [Epic]/[Tracking] title, written only by trusted
# maintainers (issue-trust.sh — ADR-0026), no open PR and no `.claude/worktrees/ticket-<n>` yet
# (the PR waits for the owner's review, the worktree means a session is on it — ADR-0060) unless
# that worktree was kept for a resume after a usage limit (work-ticket.sh then decides — #934),
# and every "Depends on #X" reference in the body points at a CLOSED issue (a ticket is
# closed by its merged PR, so closed == merged in this repo's workflow). Order: priority label (priority-critical > -high > -medium > -low > none), then issue
# number. Label taxonomy: docs/labels.md (kept in sync with TheKittySaver).
#
# Usage: next-ticket.sh [--exclude "296 293"]     # numbers already attempted this run
# Env:   LOOP_SKIP_LABELS   comma-separated labels that exclude a ticket
#                           (default: loop-blocked,epic,question,wontfix,invalid,duplicate,qa,
#                           qa-blocked,audit,post-mvp — an epic is a tracking parent with no work
#                           of its own, question/wontfix/invalid/duplicate are not decided work,
#                           qa passes are manual/human, audit findings are triaged by a human
#                           first, post-mvp is deliberately cut from MVP per CLAUDE.md: name one
#                           explicitly to work it. Same list as TheKittySaver plus the parking label)
#        LOOP_SKIP_TITLES   regex over titles to exclude (default: ^M4- — the desktop-app milestone
#                           targets the Windows patcher runtime; its E2E can't run on the macOS host)
#        LOOP_SKIP_ISSUES   space-separated numbers to exclude (default: 85 — the M2-18 forum
#                           watcher is deferred post-MVP; work it only by naming it explicitly)
#        see issue-trust.sh for the provenance knobs (LOOP_TRUSTED_ASSOCIATIONS / _LOGINS,
#        LOOP_TRUST_GATE)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# Resolve `gh`'s repository from this checkout, not from the caller's cwd (see issue-trust.sh).
cd "$SCRIPT_DIR/../.."
# Worktrees live under the MAIN checkout, even when this runs from a worktree (see work-ticket.sh).
WORKTREES="$(dirname "$(git rev-parse --path-format=absolute --git-common-dir)")/.claude/worktrees"

EXCLUDE=""
if [ "${1:-}" = "--exclude" ]; then
    EXCLUDE="${2:-}"
fi
EXCLUDE="$EXCLUDE ${LOOP_SKIP_ISSUES-85}"

SKIP_LABELS="${LOOP_SKIP_LABELS:-loop-blocked,epic,question,wontfix,invalid,duplicate,qa,qa-blocked,audit,post-mvp}"
SKIP_TITLES="${LOOP_SKIP_TITLES:-^M4-}"

for tool in gh jq; do
    command -v "$tool" >/dev/null 2>&1 || { echo "next-ticket: missing dependency: $tool" >&2; exit 1; }
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
    [ -f "$git_dir/loop-resume" ]
}

for n in $candidates; do
    case " $EXCLUDE " in
        *" $n "*) continue ;;
    esac
    # Its session may have opened the PR before the limit hit; work-ticket.sh tells that apart.
    if ! kept_for_resume "$n"; then
        if printf '%s\n' "$open_heads" | grep -q "^$n-"; then
            continue
        fi
        if [ -e "$WORKTREES/ticket-$n" ]; then
            continue
        fi
    fi

    # Provenance gate (ADR-0026): on a public repo anyone can write the text the worker reads as
    # its task. An issue whose author — or any of whose commenters — lacks write access is never
    # ready. work-ticket.sh re-checks this, so naming a ticket explicitly cannot bypass it.
    trust_rc=0
    "$SCRIPT_DIR/issue-trust.sh" "$n" || trust_rc=$?
    if [ "$trust_rc" -eq 2 ]; then
        # Say so loudly: an unreachable API skipping every candidate must not read as "backlog drained".
        echo "next-ticket: WARNING — could not verify #$n (GitHub API); skipping it (fail-closed)" >&2
    fi
    [ "$trust_rc" -eq 0 ] || continue

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
