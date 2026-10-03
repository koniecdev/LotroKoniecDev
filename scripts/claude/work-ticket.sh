#!/usr/bin/env bash
# Works ONE GitHub ticket in a headless Claude session inside its own git worktree, then
# judges the outcome. It stops at the pull request — nothing here merges (ADR-0060):
#   worktree from origin/main  →  claude -p "/work-ticket <n>"  →  STATUS: DONE (PR opened, waits for the owner's review)
#                                                             →  STATUS: DONE, no open PR: resume the session once to open it
#                                                             →  STATUS: BLOCKED (label `loop-blocked` + questions as issue comment)
#                                                             →  no STATUS line: resume the same session (up to LOOP_MAX_RESUMES)
#                                                             →  usage limit: keep the worktree, so the next run resumes the session
#
# The worktree is `.claude/worktrees/ticket-<n>` under the main checkout — the same name a manual
# `/ticket` session uses — so the main checkout is never touched and several tickets can run at
# once. The session dies with the ticket, so nothing accumulates anywhere. The conductor
# (backlog-loop.sh) calls this once per ticket, up to `-j` at a time, and again after a usage-limit
# nap: that run finds the kept worktree and resumes the session in it instead of starting over.
#
# Usage: work-ticket.sh <issue-number> [run-dir]
# Env:
#   LOOP_EFFORT             claude effort level (default: the worker effort from
#                           ~/.claude/model-policy.env when present, else high — reviews inside
#                           the session run at high via the code-reviewer agent definition)
#   LOOP_MODEL              model (default: the worker model from ~/.claude/model-policy.env
#                           when present, else opus — Opus 5.5 in the policy since
#                           2026-09-22)
#   LOOP_CONFIG_DIR         Claude config dir = which account runs the loop
#                           (default: ~/.claude-account1)
#   LOOP_GH_USER            gh account whose token backs the loop's gh write calls — labels and
#                           issue comments (default: koniecdev, the repo owner); an existing
#                           GH_TOKEN in the environment wins
#   LOOP_PERMISSION_MODE    default: auto, plus a loop-scoped --allowedTools Bash allowlist
#                           (git/gh/dotnet/scripts — see LOOP_ALLOWED_TOOLS below); this does NOT
#                           widen permissions of your interactive sessions
#   LOOP_ALLOWED_TOOLS      override the loop's Bash allowlist (space-separated rule list)
#   LOOP_UNSAFE=1           use --dangerously-skip-permissions instead (full overnight autonomy)
#   LOOP_MAX_BUDGET_USD     optional per-ticket API budget cap; unset by default on purpose (why:
#                           docs/claude-loop.md, #953)
#   LOOP_TICKET_TIMEOUT_MIN wall-clock kill switch per run of this script, resumes included
#                           (default: 240, only a guard against a stuck session — why:
#                           docs/claude-loop.md, #953); the run that resumes after a usage limit
#                           gets a new one
#   LOOP_MAX_RESUMES        how many times a session that ends normally without a STATUS line is
#                           resumed before the ticket counts as an error (default: 2); the one
#                           resume of a DONE with no open PR and the resume after a usage limit
#                           are not counted here
#   BASH_MAX_TIMEOUT_MS     the longest Bash timeout the session may ask for (default here:
#                           3600000, one hour), so the whole test suite fits in one foreground call
#   BASH_DEFAULT_TIMEOUT_MS the timeout of a Bash call that names none (default here: 600000,
#                           ten minutes), because a call that runs out is stopped, not moved
#   LOOP_KEEP_WORKTREE=1    keep the worktree after the run (default: remove it when it is clean;
#                           the branch always stays)
#   LOOP_TRUSTED_ASSOCIATIONS / LOOP_TRUSTED_LOGINS / LOOP_TRUST_GATE — see issue-trust.sh
#
# Exit codes: 0 PR opened · 2 blocked · 3 error (incl. a provenance gate that could not reach the API)
#             4 timeout · 6 usage limit hit (the worktree is kept when the session can be resumed)
#             10 could not prepare the worktree
#             11 issue refused by the provenance gate (untrusted author or commenter)
#             12 skipped: the ticket already has an open PR, its worktree already exists, or a
#                worktree kept for a resume can no longer be resumed safely
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$REPO_ROOT"

ISSUE="${1:?usage: work-ticket.sh <issue-number> [run-dir]}"
case "$ISSUE" in
    ''|*[!0-9]*) echo "work-ticket: not an issue number: '$ISSUE'" >&2; exit 3 ;;
esac

# Worktrees, logs and the lock live under the MAIN checkout even when this script runs from a
# worktree, so every run on the machine sees the same `.claude/worktrees/ticket-<n>` names.
MAIN_ROOT="$(dirname "$(git rev-parse --path-format=absolute --git-common-dir)")"
# The PRs of this ticket, as a jq filter: from this repository, on a branch named "<n>-…" (the name
# `gh issue develop` gives it).
OURS="select((.isCrossRepository | not) and (.headRefName | startswith(\"$ISSUE-\")))"
RUN_DIR="${2:-$MAIN_ROOT/logs/claude-loop/adhoc-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$RUN_DIR"

# Central per-role model/effort policy (maintainer's machine); explicit LOOP_* env still wins,
# and the hardcoded fallbacks keep a fresh clone self-contained.
if [ -f "$HOME/.claude/model-policy.env" ]; then
    # shellcheck disable=SC1091
    . "$HOME/.claude/model-policy.env"
fi
EFFORT="${LOOP_EFFORT:-${MODEL_POLICY_WORKER_EFFORT:-high}}"
MODEL="${LOOP_MODEL:-${MODEL_POLICY_WORKER_MODEL:-opus}}"
export CLAUDE_CONFIG_DIR="${LOOP_CONFIG_DIR:-$HOME/.claude-account1}"
PERMISSION_MODE="${LOOP_PERMISSION_MODE:-auto}"
TIMEOUT_MIN="${LOOP_TICKET_TIMEOUT_MIN:-240}"
KEEP_WORKTREE="${LOOP_KEEP_WORKTREE:-0}"
MAX_RESUMES="${LOOP_MAX_RESUMES:-2}"
# A resume needs time for at least the test suite and the push.
MIN_RESUME_MIN=10

OUT="$RUN_DIR/ticket-$ISSUE.json"
ERR="$RUN_DIR/ticket-$ISSUE.stderr"
META="$RUN_DIR/ticket-$ISSUE.meta"
WT="$MAIN_ROOT/.claude/worktrees/ticket-$ISSUE"

for tool in claude gh jq git; do
    command -v "$tool" >/dev/null 2>&1 || { echo "work-ticket: missing dependency: $tool" >&2; exit 3; }
done

log() { echo "[loop] #$ISSUE $(date +%H:%M:%S) $*"; }

meta() { echo "$1=$2" >> "$META"; }

# Every exit path writes its outcome here, so the conductor's roll-up lists skipped and refused
# tickets too, not only the ones that got a session.
: > "$META"
meta issue "$ISSUE"

case "$MAX_RESUMES" in
    ''|*[!0-9]*)
        meta outcome error
        echo "work-ticket: LOOP_MAX_RESUMES is not a number: '$MAX_RESUMES'" >&2
        exit 3
        ;;
esac

# The clock is first used in shell arithmetic after the session has started. There a bad value
# ends this script with no outcome and leaves the worktree behind. A leading zero is refused too:
# the shell reads "090" as an octal number.
case "$TIMEOUT_MIN" in
    ''|*[!0-9]*|0*)
        meta outcome error
        echo "work-ticket: LOOP_TICKET_TIMEOUT_MIN is not a whole number of minutes above zero, without a leading zero: '$TIMEOUT_MIN'" >&2
        exit 3
        ;;
esac

# ── gh identity: write calls must not depend on the machine's ACTIVE gh account ────────────────
# Labels and issue comments need write access, but the active gh account here is often the EMU
# work account, which GitHub bars from writing outside its enterprise ("Enterprise Managed User
# cannot access this content"). Mint the owner's token unless the caller set one.
if [ -z "${GH_TOKEN:-}" ]; then
    owner_token="$(gh auth token --user "${LOOP_GH_USER:-koniecdev}" 2>/dev/null || true)"
    if [ -n "$owner_token" ]; then
        export GH_TOKEN="$owner_token"
    else
        log "no gh token for '${LOOP_GH_USER:-koniecdev}' — gh write calls will use the active gh account"
    fi
fi

# A rebase, merge or cherry-pick the session left half done. A commit on top of it would bury the
# conflict, so such a worktree is left exactly as it is, for a human.
operation_in_progress() {
    local marker
    for marker in rebase-merge rebase-apply MERGE_HEAD CHERRY_PICK_HEAD REVERT_HEAD; do
        [ -e "$(git -C "$WT" rev-parse --path-format=absolute --git-path "$marker")" ] && return 0
    done
    return 1
}

# Commit (never delete, never stash) anything a session left behind: leftovers become ordinary
# named git history on a dedicated `loop-salvage/<issue>-<timestamp>` branch, cut from wherever
# the session got to (so partial commits on the ticket branch stay reachable from it too).
# Removing a worktree also drops its reflog, so a commit that no branch and no remote-tracking ref
# reaches (one made while detached) gets a salvage branch too. Returns 1 when it could not make
# the worktree safe to remove.
salvage() {
    [ -d "$WT" ] || return 0
    local salvage_branch
    salvage_branch="loop-salvage/$ISSUE-$(date +%Y%m%d-%H%M%S)"
    if [ -n "$(git -C "$WT" status --porcelain)" ]; then
        git -C "$WT" checkout --quiet -b "$salvage_branch" 2>/dev/null || return 1
        git -C "$WT" add -A >/dev/null 2>&1 || true
        git -C "$WT" commit --quiet --no-verify \
            -m "claude-loop: salvage uncommitted work for #$ISSUE" \
            -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" >/dev/null 2>&1 || return 1
        log "leftover changes committed on branch $salvage_branch"
        return 0
    fi
    if [ -z "$(git -C "$WT" for-each-ref --contains HEAD --format='%(refname)' refs/heads refs/remotes 2>/dev/null)" ]; then
        git -C "$WT" branch "$salvage_branch" HEAD 2>/dev/null || return 1
        log "commits made on no branch kept on branch $salvage_branch"
    fi
}

# Each E2E suite tags its images per worktree as `<repository>:<suite>-<folder>-<hash>` (#884), and
# nothing removes them with the worktree. A loop run removes its worktree, so it removes them too.
remove_e2e_images() {
    command -v docker >/dev/null 2>&1 || return 0
    local images image
    images="$(docker image ls --filter label=lotrokoniecdev.e2e --format '{{.Repository}}:{{.Tag}}' 2>/dev/null \
        | grep -F -- "-ticket-$ISSUE-" || true)"
    for image in $images; do
        docker image rm "$image" >/dev/null 2>&1 || true
    done
}

# Everything is committed by now, so removing a clean worktree loses nothing: the branch stays,
# and `git worktree add` brings the folder back in seconds for a follow-up fix.
# finish [remove] — "remove" removes the worktree even with LOOP_KEEP_WORKTREE=1: a kept worktree
# that can no longer be resumed makes room for a fresh start.
finish() {
    [ -d "$WT" ] || return 0
    if operation_in_progress; then
        log "worktree left as it is — a rebase or merge is half done in $WT. Finish or abort it, then: git worktree remove \"$WT\""
        return 0
    fi
    if ! salvage; then
        log "worktree left in place — its leftovers could not be committed: $WT"
        return 0
    fi
    if [ "$KEEP_WORKTREE" = "1" ] && [ "${1:-}" != "remove" ]; then
        log "worktree kept: $WT"
        return 0
    fi
    if git worktree remove "$WT" 2>/dev/null; then
        remove_e2e_images
    else
        log "worktree left in place (not clean): $WT"
    fi
}

# ── A worktree kept after a usage limit (#934) ─────────────────────────────────────────────────
# On a usage limit the worktree stays exactly as the session left it, uncommitted files included,
# and a marker in the worktree's own git folder names the session. Salvage would move those files
# to another branch, so a re-created worktree would not be the tree the session knows. The marker
# goes away with the worktree. next-ticket.sh reads the same marker, so change both together.

# Prints the marker's path. A folder that is not a worktree of its own resolves to the main .git,
# so it has none.
kept_marker() {
    local git_dir
    [ -d "$WT" ] || return 1
    git_dir="$(git -C "$WT" rev-parse --path-format=absolute --git-dir 2>/dev/null)" || return 1
    [ "$git_dir" != "$(git -C "$WT" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)" ] || return 1
    echo "$git_dir/loop-resume"
}

# Every file of the worktree that git does not ignore, committed or not, as one tree id. It is built
# in a copy of the index, so the worktree's own index and `git status` stay as they are.
tree_state() {
    local index state=""
    index="$(mktemp "${TMPDIR:-/tmp}/loop-index.XXXXXX")" || return 1
    cp "$(git -C "$WT" rev-parse --path-format=absolute --git-path index)" "$index" 2>/dev/null || rm -f "$index"
    if GIT_INDEX_FILE="$index" git -C "$WT" add -A >/dev/null 2>&1; then
        state="$(GIT_INDEX_FILE="$index" git -C "$WT" write-tree 2>/dev/null || true)"
    fi
    rm -f "$index"
    [ -n "$state" ] || return 1
    echo "$state"
}

# Writes the marker for the session; fails when there is nothing to resume.
keep_for_resume() {
    local marker head tree
    [ -n "$known_session" ] || return 1
    marker="$(kept_marker)" || return 1
    head="$(git -C "$WT" rev-parse HEAD 2>/dev/null)" || return 1
    tree="$(tree_state)" || return 1
    printf 'session=%s\nhead=%s\ntree=%s\nturns=%s\nstarted=%s\n' \
        "$known_session" "$head" "$tree" "$turns" "$session_started" > "$marker" 2>/dev/null || return 1
}

# The branch of the kept worktree. A rebase that stopped half way detaches HEAD, but git still
# records which branch it rebases.
kept_branch() {
    local branch state file
    branch="$(git -C "$WT" branch --show-current 2>/dev/null || true)"
    if [ -z "$branch" ]; then
        for state in rebase-merge rebase-apply; do
            file="$(git -C "$WT" rev-parse --path-format=absolute --git-path "$state/head-name" 2>/dev/null || true)"
            if [ -n "$file" ] && [ -f "$file" ]; then
                branch="$(sed 's#^refs/heads/##' "$file")"
                break
            fi
        done
    fi
    echo "$branch"
}

# `--resume` finds a session only in the config dir that ran it, and only until the CLI's cleanup
# deletes its transcript (30 days by default).
has_transcript() {
    local file
    [ -n "$1" ] || return 1
    for file in "$CLAUDE_CONFIG_DIR"/projects/*/"$1".jsonl; do
        [ -f "$file" ] && return 0
    done
    return 1
}

# The marker is read once: another run may claim it at any moment, and a read that fails half way
# must not end this script under `set -e`.
marker="$(kept_marker || true)"
marker_text=""
if [ -n "$marker" ]; then
    marker_text="$(cat "$marker" 2>/dev/null || true)"
fi
marker_value() {
    sed -n "s/^$1=//p" <<< "$marker_text" | tail -1
}

# ── Provenance gate: only maintainer-written issue text may become the worker's task ───────────
# This is the enforcement point, not next-ticket.sh: the picker merely *selects*, whereas the
# untrusted text reaches the LLM here. Explicit ticket numbers (backlog-loop.sh 123) and direct
# invocations skip the picker entirely, so the gate has to live in front of the session (ADR-0026).
trust_rc=0
"$REPO_ROOT/scripts/claude/issue-trust.sh" "$ISSUE" || trust_rc=$?
if [ "$trust_rc" -eq 1 ]; then
    meta outcome untrusted
    # A session kept for a resume will never run again, so its work is salvaged now, as it was
    # before #934, instead of waiting in a worktree nobody is told about.
    if [ -n "$marker_text" ]; then
        rm -f "$marker"
        finish remove
    fi
    log "REFUSED by the provenance gate — untrusted writer (see above); no session spawned"
    exit 11
fi
if [ "$trust_rc" -ne 0 ]; then
    # An unreadable API is systemic, not a property of this ticket: report it as a session error
    # so the conductor's circuit breaker stops the run instead of "skipping" the whole backlog.
    meta outcome error
    log "provenance gate could not verify #$ISSUE (rc=$trust_rc) — treating as an error"
    exit 3
fi

# ── Resume a kept worktree, or never start a ticket that is already in flight ──────────────────
# A worktree folder deleted by hand stays registered, and `git worktree add` then refuses the path.
git worktree prune 2>/dev/null || true

resume_session=""
resume_turns=0
resume_started=""
release_kept=0
if [ -n "$marker_text" ]; then
    kept_session="$(marker_value session)"
    current_tree="$(tree_state)" || {
        meta outcome error
        log "could not read the files of the kept worktree $WT — treating as an error; the next run tries again"
        exit 3
    }
    # The session must find the tree it left. A new commit or a changed file means someone else
    # works there now.
    if [ "$(git -C "$WT" rev-parse HEAD 2>/dev/null || true)" != "$(marker_value head)" ] \
        || [ "$current_tree" != "$(marker_value tree)" ]; then
        rm -f "$marker"
        meta outcome skipped
        log "SKIPPED — $WT was kept to resume a session after a usage limit, but its HEAD or its files changed since, so someone else works there. When that work is done: git worktree remove \"$WT\""
        exit 12
    fi
    if has_transcript "$kept_session"; then
        resume_session="$kept_session"
        resume_turns="$(marker_value turns)"
        case "$resume_turns" in ''|*[!0-9]*) resume_turns=0 ;; esac
        resume_started="$(marker_value started)"
        case "$resume_started" in ''|*[!0-9]*) resume_started=0 ;; esac
    else
        log "the kept session ${kept_session:-<none>} has no transcript under $CLAUDE_CONFIG_DIR, so it cannot be resumed"
        release_kept=1
    fi
fi

if [ -n "$resume_session" ]; then
    # The session may have opened its PR before the limit hit, so an open PR from the kept branch
    # is its own, as long as nobody pushed to it since. Any other PR of the ticket that is open, or
    # that was merged or closed after the session started, means the work went on without it.
    branch="$(kept_branch)"
    ticket_prs="$(gh pr list --state all --limit 200 --json number,headRefName,headRefOid,isCrossRepository,state,mergedAt,closedAt \
        --jq ".[] | $OURS | \"\(.number) \(.state) \(.headRefName) \(.headRefOid) \((.mergedAt // .closedAt // \"\") | if . == \"\" then 0 else fromdateiso8601 end)\"")" || {
        meta outcome error
        log "could not list the ticket's pull requests — treating as an error"
        exit 3
    }
    blocker=""
    while read -r number state head oid ended; do
        [ -n "$number" ] || continue
        case "$state" in
            OPEN)
                if [ "$head" != "$branch" ]; then
                    blocker="PR #$number is open from another branch ($head)"
                elif ! git -C "$WT" merge-base --is-ancestor "$oid" "refs/heads/$branch" 2>/dev/null \
                    && ! grep -qx "$oid" <<< "$(git -C "$WT" reflog show --format=%H "refs/heads/$branch" 2>/dev/null || true)"; then
                    # A review fix or a rebase by /merge-train: the session would build on a stale
                    # copy, and its next push could overwrite that work. A head the branch itself
                    # once had is the session's own push, before it rewrote the branch (a rebase
                    # before the force push); nobody else's push ever enters this reflog.
                    blocker="PR #$number has commits the kept branch does not"
                fi
                ;;
            MERGED)
                [ "$ended" -lt "$resume_started" ] || blocker="PR #$number was merged"
                ;;
            CLOSED)
                if [ "$head" = "$branch" ] && [ "$ended" -ge "$resume_started" ]; then
                    blocker="PR #$number from this branch was closed"
                fi
                ;;
        esac
        [ -z "$blocker" ] || break
    done <<< "$ticket_prs"
    if [ -n "$blocker" ]; then
        # The work went on without the session, so this will not change. Without the marker the
        # picker stops offering the ticket, and the worktree waits for a human like any other.
        rm -f "$marker"
        meta outcome skipped
        log "SKIPPED — $WT was kept to resume session $resume_session, but $blocker. Not resuming. When you are done with it: git worktree remove \"$WT\""
        exit 12
    fi
else
    # A ticket with an open PR is waiting for the owner's review, and an existing worktree means a
    # manual `/ticket` session or an earlier run is still on it. Working it again would open a
    # second PR for the same ticket, or fight over the same branch.
    open_pr="$(gh pr list --state open --limit 200 --json number,headRefName,isCrossRepository \
        --jq "[.[] | $OURS | .number] | first // empty")" || {
        meta outcome error
        log "could not list open pull requests — treating as an error"
        exit 3
    }
    # The marker goes only now: when GitHub cannot answer, the next run must still see a kept
    # worktree, not one that looks like someone's work.
    [ "$release_kept" -eq 0 ] || rm -f "$marker"
    if [ -n "$open_pr" ]; then
        meta outcome skipped
        meta pr "$open_pr"
        if [ "$release_kept" -eq 1 ]; then
            log "SKIPPED — PR #$open_pr is already open for this ticket and waits for your review; the unfinished work of its session stays in $WT for you"
        else
            log "SKIPPED — PR #$open_pr is already open for this ticket and waits for your review"
        fi
        exit 12
    fi
    if [ "$release_kept" -eq 1 ]; then
        log "salvaging the kept worktree and starting fresh"
        finish remove
    fi
    if [ -e "$WT" ]; then
        meta outcome skipped
        log "SKIPPED — $WT already exists: a session may be on this ticket. If none is, remove it with: git worktree remove \"$WT\""
        exit 12
    fi
fi

# Every process below $1. It must be read while $1 still runs: once it exits, its children move to
# PID 1 and can no longer be found from it.
descendants() {
    local kid
    for kid in $(pgrep -P "$1" 2>/dev/null); do
        echo "$kid"
        descendants "$kid"
    done
}

# Ends a session and everything it started. Claude Code runs each Bash command in a process group
# of its own, so the session's own group holds claude but not its builds and test runs. On TERM,
# claude ends those itself, so it gets $2 seconds to do that before anything is killed. Whatever
# process group of its tree (read before the TERM) is still there after that is ended as well.
end_session_tree() {
    local leader="$1" grace="$2" groups kid group tries=0
    groups="$(for kid in $(descendants "$leader"); do ps -o pgid= -p "$kid" 2>/dev/null; done | tr -d ' ' | sort -u)"
    kill -TERM -- "-$leader" 2>/dev/null || true
    while kill -0 "$leader" 2>/dev/null && [ "$tries" -lt $(( grace * 2 )) ]; do
        sleep 0.5
        tries=$((tries + 1))
    done
    for group in $groups; do
        [ "$group" = "$leader" ] || kill -TERM -- "-$group" 2>/dev/null || true
    done
    sleep 1
    for group in $groups; do
        [ "$group" = "$leader" ] || kill -KILL -- "-$group" 2>/dev/null || true
    done
    kill -KILL -- "-$leader" 2>/dev/null || true
}

# A background child of this script ignores SIGINT and would outlive it, so the session is stopped
# on every way out. Bash reaps a finished background job at once, not at `wait`, and from then on
# the system may give the session's number to another program (#983). It cannot do that while a
# process is still in the session's group, which has the same number, and the watchdog stays in
# that group until the group is killed. A stop starts only while bash still lists the session as
# running, so it leans on the watchdog only if the session ends at that very moment. The steps
# after the session's end always lean on it: the sweep after `wait`, and the last steps of a stop.
pid=""
sleeper=""

# Reads this shell's own job list. Bash takes the job off the running list in the same step in
# which it reaps it. A stopped job is not on that list either, but with job control off (`set +m`)
# bash never sees the session stop.
session_running() {
    local running
    [ -n "$pid" ] || return 1
    running="$(jobs -rp)"
    case $'\n'"$running"$'\n' in *$'\n'"$pid"$'\n'*) return 0 ;; esac
    return 1
}

stop_session() {
    [ -n "$pid" ] || return 0
    if session_running; then
        end_session_tree "$pid" 20
    fi
    wait "$pid" 2>/dev/null || true
    pid=""
}

stop_sleeper() {
    [ -n "$sleeper" ] || return 0
    kill "$sleeper" 2>/dev/null || true
    sleeper=""
}

# Closing a terminal sends HUP to the job and then TERM from the conductor, so further signals are
# ignored while the first one cleans up; otherwise the second would kill this script half way.
on_stop_signal() {
    trap '' INT TERM HUP
    stop_sleeper
    stop_session
    meta outcome stopped
    finish
    log "STOPPED — session killed, changes salvaged"
    exit 143
}
# Set before the fetch, so a stop while the worktree is being made still cleans it up.
trap 'stop_sleeper; stop_session' EXIT
trap on_stop_signal INT TERM HUP

if [ -n "$resume_session" ]; then
    # Claimed with one rename, so two runs can never resume the same session at once: the CLI would
    # interleave both into one transcript.
    if ! mv "$marker" "$marker.claimed" 2>/dev/null; then
        meta outcome skipped
        log "SKIPPED — another run has just claimed the kept session $resume_session"
        exit 12
    fi
    rm -f "$marker.claimed"
else
    # ── A fresh worktree from origin/main ──────────────────────────────────────────────────────
    # Detached on purpose: the worker creates the ticket branch itself (`gh issue develop`).
    # Several runs fetch at once, and a fetch that loses the ref lock to another one succeeds on a
    # retry.
    if ! git fetch --quiet origin main 2>/dev/null; then
        sleep 5
        git fetch --quiet origin main || { meta outcome no-worktree; log "could not fetch origin/main"; exit 10; }
    fi
    if ! git worktree add --quiet --detach "$WT" origin/main; then
        meta outcome no-worktree
        log "could not create the worktree $WT"
        exit 10
    fi
fi

# ── One headless session for the whole ticket ──────────────────────────────────────────────────
ALLOWED_TOOLS="${LOOP_ALLOWED_TOOLS:-Bash(git:*) Bash(gh:*) Bash(dotnet:*) Bash(scripts/:*) Bash(./scripts/:*)}"

session_flags=(--output-format json --model "$MODEL" --effort "$EFFORT")
if [ "${LOOP_UNSAFE:-0}" = "1" ]; then
    session_flags+=(--dangerously-skip-permissions)
else
    # shellcheck disable=SC2086,SC2206
    session_flags+=(--permission-mode "$PERMISSION_MODE" --allowedTools $ALLOWED_TOOLS)
fi
[ -n "${LOOP_MAX_BUDGET_USD:-}" ] && session_flags+=(--max-budget-usd "$LOOP_MAX_BUDGET_USD")

# `claude -p` ends a background shell about five seconds after its final message, and nothing can
# wake the session after that. Two workers lost finished tickets this way: they started the test
# suite in the background and ended their turn to wait for it (#925). With this switch the Bash
# tool has no `run_in_background`, and a command that reaches its timeout is stopped instead of
# moved to the background. So the timeout ceiling is raised to fit the whole suite, and a call
# that names no timeout (a cold build, a CodeQL wait) gets ten minutes instead of two.
export CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1
export BASH_MAX_TIMEOUT_MS="${BASH_MAX_TIMEOUT_MS:-3600000}"
export BASH_DEFAULT_TIMEOUT_MS="${BASH_DEFAULT_TIMEOUT_MS:-600000}"

# resume_prompt <no-status|no-pr|limit> <minutes left> — the prompt of a resume, on one line.
resume_prompt() {
    local why
    case "$1" in
        no-status) why="Your last message has no STATUS line, so the loop cannot tell how ticket #$ISSUE ended." ;;
        no-pr) why="Your last message says STATUS: DONE, but the loop found no open pull request for ticket #$ISSUE${pr_named:+ (PR #$pr_named, which your message links, is not one)}. \
The loop counts only an open PR in this repository whose branch name starts with \"$ISSUE-\". \
Push the branch and open the PR now, or name the full URL of that PR if it already exists." ;;
        limit) why="The usage limit that stopped this session is over, and the loop resumes ticket #$ISSUE in the same worktree. \
Time has passed since your last step, so check git status and whether the PR already exists before you go on. \
A command that was running when the limit hit did not finish: run it again." ;;
    esac
    echo "$why \
This is a headless run: when you end your turn, the process exits and nothing wakes you again. \
Background runs are switched off here. If you were waiting for something, such as the test suite, \
run it again now in the foreground, with a Bash timeout long enough for it (at most $BASH_MAX_TIMEOUT_MS ms). \
The loop stops this session in about $2 minutes. \
Then finish the /work-ticket steps that are still open (commit, push, PR, CodeQL, the plain-English pass) \
and end with the STATUS: DONE or STATUS: BLOCKED block from /work-ticket, with nothing after it."
}

# run_session <output file> <prompt> [more claude arguments] — one headless run in the worktree;
# sets claude_rc. The wall clock belongs to the ticket, so a resume gets only what is left of it.
run_session() {
    local out="$1" cmd=(claude -p "$2" "${session_flags[@]}" "${@:3}")
    set +e
    # `set -m` gives the session its own process group. It also stops bash from pointing a
    # background job's stdin at /dev/null, so that is done by hand: a job outside the terminal's
    # foreground group that reads the terminal is suspended. A SIGKILL to this script runs no trap,
    # and the session is no longer in this script's group, so a watchdog inside the group ends the
    # session once this script is gone. The session could otherwise go on working and pushing with
    # nothing watching it. The watchdog ignores TERM: the TERM it sends to its own group must not
    # end it half way.
    set -m
    (
        set +m  # keep the watchdog in the session's group, so the group's end is its end too
        group="$(exec sh -c 'echo "$PPID"')"
        (
            trap '' TERM
            while kill -0 "$$" 2>/dev/null; do sleep 5; done
            end_session_tree "$group" 15
        ) < /dev/null > /dev/null 2>&1 &
        # Only now: a session that cannot start must still leave the watchdog in its group (#983).
        cd "$WT" || exit 1
        exec "${cmd[@]}"
    ) < /dev/null > "$out" 2>> "$ERR" &
    pid=$!
    set +m
    while session_running; do
        # A background sleep + wait, so a stop signal runs its trap at once instead of after the nap.
        sleep 30 &
        sleeper=$!
        wait "$sleeper"
        sleeper=""
        # A session that ended in the nap is judged from its result, even past the limit (#991).
        session_running || break
        if [ $(( $(date +%s) - start_epoch )) -ge $(( TIMEOUT_MIN * 60 )) ]; then
            stop_session
            set -e
            meta outcome timeout
            finish
            log "TIMEOUT after ${TIMEOUT_MIN}m — session killed, changes salvaged"
            exit 4
        fi
    done
    wait "$pid"
    claude_rc=$?
    # claude ends its own commands when it exits normally; what is left in the session's own group
    # (the watchdog, which ignores TERM, or a plain child) ends here.
    kill -KILL -- "-$pid" 2>/dev/null || true
    pid=""
    set -e
}

# keep_result <k> — a resume wrote $OUT.resume-<k>. Copy, then rename over: a stop between the two
# steps still leaves a result in $OUT.
keep_result() {
    [ ! -e "$OUT" ] || cp "$OUT" "$OUT.before-resume-$1"
    mv "$OUT.resume-$1" "$OUT"
}

# find_ticket_pr <final message> — sets pr_num to this ticket's open PR. Without one it sets
# pr_problem for the log, pr_named to the number the message links, and pr_unknown=1 when GitHub
# could not say whether there is one. pr_problem holds the linked PR's branch, which anyone who
# opens a PR can name, so only pr_named may reach a prompt (ADR-0026).
find_ticket_pr() {
    local url state
    pr_num=""
    pr_named=""
    pr_problem=""
    pr_unknown=0
    url="$(grep -oE 'https://github\.com/[^ )>,]+/pull/[0-9]+' <<< "$1" | head -1 || true)"
    if [ -n "$url" ]; then
        pr_named="${url##*/}"
        state="$(gh pr view "$pr_named" --json state,isCrossRepository,headRefName \
            --jq '"\(.state) \(.isCrossRepository) \(.headRefName)"' 2>/dev/null || true)"
        case "$state" in
            "OPEN false $ISSUE-"*) pr_num="$pr_named"; return 0 ;;
        esac
        pr_problem="PR #$pr_named is not an open PR for this ticket (${state:-unreadable})"
    fi
    # A wrong link may be a slip in the summary: the ticket's own PR, found by its branch, counts.
    if ! pr_num="$(gh pr list --state open --limit 200 --json number,headRefName,isCrossRepository \
        --jq "[.[] | $OURS | .number] | first // empty")"; then
        pr_num=""
        pr_unknown=1
        pr_problem="${pr_problem:-no PR named}, and the open PRs could not be listed"
        return 0
    fi
    if [ -n "$pr_num" ]; then
        pr_problem=""
    else
        pr_problem="${pr_problem:-no PR found}"
    fi
}

start_epoch="$(date +%s)"
resumes=0
turns="$resume_turns"
# The last session id a run reported. A run that crashes without JSON must not lose it.
known_session="$resume_session"
# When the session first started, carried across limits: a PR that ended before it is history.
session_started="${resume_started:-$start_epoch}"
[ -n "$resume_session" ] || session_started="$start_epoch"
if [ -n "$resume_session" ]; then
    # The results of the attempt the limit stopped stay for debugging, under names the conductor's
    # cost total does not read. $OUT stays until the resume ends: the resume reports the whole
    # session's cost, the earlier part included. The old stderr moves too, or its limit message
    # would make a later crash look like a usage limit.
    stamp="$(date +%Y%m%d-%H%M%S)"
    for old in "$OUT".before-resume-* "$OUT".resume-* "$ERR"; do
        [ -e "$old" ] || continue
        case "$old" in *.limit-*) continue ;; esac
        mv "$old" "$old.limit-$stamp"
    done
    : > "$ERR"
    resumes=1
    meta resumes 1
    log "resuming session $resume_session after a usage limit, in the kept worktree $WT (model=$MODEL, effort=$EFFORT, timeout=${TIMEOUT_MIN}m)"
    run_session "$OUT.resume-1" "$(resume_prompt limit "$TIMEOUT_MIN")" --resume "$resume_session"
    keep_result 1
else
    log "fresh headless session starting in $WT (model=$MODEL, effort=$EFFORT, timeout=${TIMEOUT_MIN}m)"
    # A usage-limit retry that could not resume runs the ticket again in the same run folder: start
    # from clean logs.
    : > "$ERR"
    rm -f "$OUT".before-resume-* "$OUT".resume-* "$ERR".limit-*
    meta resumes 0
    run_session "$OUT" "/work-ticket $ISSUE"
fi

# ── A session that stopped without a verdict is resumed ────────────────────────────────────────
# A session that ended normally (no crash, no usage limit) but printed no STATUS line has usually
# stopped to wait for something. One that says DONE may still have no open PR: a push or
# `gh pr create` failed. Its commits are in the worktree, so a short prompt to the same session is
# cheaper than losing the ticket. A resume writes its own file and replaces $OUT only when it ends:
# a resume that is killed leaves the last result, and its cost, in place. Earlier results move to
# a name the conductor's cost total does not read, because a resumed run reports the cost of the
# whole session (checked against the CLI; --max-budget-usd counts it too).
status_resumes=0
pr_resumed=0
pr_num=""
pr_named=""
pr_problem=""
pr_unknown=0
while :; do
    run_session_id="$(jq -r '.session_id // ""' "$OUT" 2>/dev/null || true)"
    [ -z "$run_session_id" ] || known_session="$run_session_id"
    run_turns="$(jq -r '.num_turns // 0' "$OUT" 2>/dev/null || echo 0)"
    case "$run_turns" in ''|*[!0-9]*) run_turns=0 ;; esac
    turns=$((turns + run_turns))
    [ "$claude_rc" -eq 0 ] || break
    [ "$(jq -r '.is_error // false' "$OUT" 2>/dev/null || echo true)" = "false" ] || break
    [ "$(jq -r '.api_error_status // 0' "$OUT" 2>/dev/null || echo 0)" != "429" ] || break
    run_result="$(jq -r '.result // ""' "$OUT" 2>/dev/null || true)"
    if grep -qE '^STATUS:[[:space:]]*BLOCKED' <<< "$run_result"; then
        break
    fi
    if grep -qE '^STATUS:[[:space:]]*DONE' <<< "$run_result"; then
        find_ticket_pr "$run_result"
        [ -n "$pr_problem" ] || break
        # Only a real "no PR" from GitHub is worth a resume, and only once.
        [ "$pr_unknown" -eq 0 ] || break
        [ "$pr_resumed" -eq 0 ] || break
        reason=no-pr
    else
        [ "$status_resumes" -lt "$MAX_RESUMES" ] || break
        reason=no-status
    fi
    [ -n "$run_session_id" ] || break
    left_min=$(( TIMEOUT_MIN - ( $(date +%s) - start_epoch ) / 60 ))
    if [ "$left_min" -lt "$MIN_RESUME_MIN" ]; then
        log "the session needs a resume, but only ${left_min}m of the ticket's clock is left — not resuming"
        break
    fi
    # A resume is a new process that may read the issue again, so the gate runs in front of it too
    # (ADR-0026): a stranger's comment added since the start must not reach the session.
    trust_rc=0
    "$REPO_ROOT/scripts/claude/issue-trust.sh" "$ISSUE" || trust_rc=$?
    if [ "$trust_rc" -eq 1 ]; then
        meta outcome untrusted
        finish
        log "REFUSED by the provenance gate before a resume — untrusted writer (see above)"
        exit 11
    fi
    if [ "$trust_rc" -ne 0 ]; then
        log "provenance gate could not verify #$ISSUE before a resume (rc=$trust_rc) — not resuming"
        break
    fi
    resumes=$((resumes + 1))
    meta resumes "$resumes"
    if [ "$reason" = "no-pr" ]; then
        pr_resumed=1
        log "the session reported DONE, but $pr_problem — resuming it once to open the PR"
    else
        status_resumes=$((status_resumes + 1))
        log "the session stopped without a STATUS line — resuming it ($status_resumes of $MAX_RESUMES)"
    fi
    run_session "$OUT.resume-$resumes" "$(resume_prompt "$reason" "$left_min")" --resume "$run_session_id"
    keep_result "$resumes"
done

elapsed_min=$(( ( $(date +%s) - start_epoch ) / 60 ))

result="$(jq -r '.result // ""' "$OUT" 2>/dev/null || echo "")"
is_error="$(jq -r '.is_error // false' "$OUT" 2>/dev/null || echo "true")"
cost="$(jq -r '.total_cost_usd // 0' "$OUT" 2>/dev/null || echo 0)"
meta cost "$cost"
meta turns "$turns"
meta minutes "$elapsed_min"
[ -z "$known_session" ] || meta session "$known_session"

# ── Usage-limit / hard-error detection ─────────────────────────────────────────────────────────
# The CLI reports plan/rate limits as api_error_status 429 in the result JSON regardless of the
# message wording ("usage limit", "session limit", …) — trust that first. The wording grep is the
# fallback for a failed session only (a limit stop always sets is_error): a session that ended
# normally may well talk about rate limits (this repo has many such tickets), with or without a
# STATUS block, and that must not read as a limit hit.
api_error_status="$(jq -r '.api_error_status // 0' "$OUT" 2>/dev/null || echo 0)"
combined="$result $(tail -c 2000 "$ERR" 2>/dev/null || true)"
session_failed=0
if [ "$claude_rc" -ne 0 ] || [ "$is_error" = "true" ]; then
    session_failed=1
fi
if [ "$api_error_status" = "429" ] \
    || { [ "$session_failed" -eq 1 ] && echo "$combined" | grep -qiE 'usage limit|session limit|rate.?limit|overloaded|quota'; }; then
    meta outcome limit
    if keep_for_resume; then
        meta worktree kept
        log "USAGE LIMIT hit — worktree kept, so the next run of #$ISSUE resumes session $known_session; the conductor will sleep and retry"
        exit 6
    fi
    finish
    log "USAGE LIMIT hit — no session to resume, so the retry starts over; the conductor will sleep and retry"
    exit 6
fi
if [ "$claude_rc" -ne 0 ] || [ "$is_error" = "true" ]; then
    meta outcome error
    finish
    log "session ERROR (rc=$claude_rc, is_error=$is_error) — see $ERR"
    exit 3
fi

# ── Judge the worker's final message (STATUS contract from /work-ticket) ───────────────────────
if echo "$result" | grep -qE '^STATUS:[[:space:]]*BLOCKED'; then
    meta outcome blocked
    gh label create loop-blocked --color e36209 \
        --description "claude-loop: needs human input" --force >/dev/null 2>&1 || true
    gh issue edit "$ISSUE" --add-label loop-blocked >/dev/null 2>&1 || true
    comment="$(printf '🤖 **claude-loop: BLOCKED** — needs your input before the loop retries this ticket.\n\n%s' \
        "$(echo "$result" | head -c 4000)")"
    gh issue comment "$ISSUE" --body "$comment" >/dev/null 2>&1 || true
    finish
    log "BLOCKED — questions posted on the issue, labeled loop-blocked"
    exit 2
fi

if ! echo "$result" | grep -qE '^STATUS:[[:space:]]*DONE'; then
    meta outcome error
    finish
    log "no STATUS: DONE/BLOCKED contract in the final message after $resumes resume(s) — treating as error (see $OUT)"
    exit 3
fi

# ── DONE: the PR must really exist (never trust a summary alone) ───────────────────────────────
# The loop above already asked GitHub for the final message. The number in that message is only a
# hint: it must be an open PR in this repo whose branch belongs to this ticket. A made-up link, or
# a link to another PR that the summary happens to mention first, never counts as this ticket's PR.
if [ -z "$pr_num" ]; then
    meta outcome error
    finish
    log "worker reported DONE, but $pr_problem — treating as error"
    exit 3
fi
meta pr "$pr_num"
meta outcome pr-opened
finish
log "PR #$pr_num opened — waiting for your review (cost \$$cost, $turns turns, ${elapsed_min}m, $resumes resume(s))"
exit 0
