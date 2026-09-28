#!/usr/bin/env bash
# Works ONE GitHub ticket in a FRESH headless Claude session inside its own git worktree, then
# judges the outcome. It stops at the pull request — nothing here merges (ADR-0060):
#   worktree from origin/main  →  claude -p "/work-ticket <n>"  →  STATUS: DONE (PR opened, waits for the owner's review)
#                                                             →  STATUS: BLOCKED (label `loop-blocked` + questions as issue comment)
#                                                             →  no STATUS line: resume the same session (up to LOOP_MAX_RESUMES)
#
# The worktree is `.claude/worktrees/ticket-<n>` under the main checkout — the same name a manual
# `/ticket` session uses — so the main checkout is never touched and several tickets can run at
# once. The session dies with the ticket, so nothing accumulates anywhere. The conductor
# (backlog-loop.sh) calls this once per ticket, up to `-j` at a time.
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
#   LOOP_MAX_BUDGET_USD     optional per-ticket API budget cap
#   LOOP_TICKET_TIMEOUT_MIN wall-clock kill switch per ticket, resumes included (default: 90)
#   LOOP_MAX_RESUMES        how many times a session that ends normally without a STATUS line is
#                           resumed before the ticket counts as an error (default: 2)
#   BASH_MAX_TIMEOUT_MS     the longest Bash timeout the session may ask for (default here:
#                           3600000, one hour), so the whole test suite fits in one foreground call
#   BASH_DEFAULT_TIMEOUT_MS the timeout of a Bash call that names none (default here: 600000,
#                           ten minutes), because a call that runs out is stopped, not moved
#   LOOP_KEEP_WORKTREE=1    keep the worktree after the run (default: remove it when it is clean;
#                           the branch always stays)
#   LOOP_TRUSTED_ASSOCIATIONS / LOOP_TRUSTED_LOGINS / LOOP_TRUST_GATE — see issue-trust.sh
#
# Exit codes: 0 PR opened · 2 blocked · 3 error (incl. a provenance gate that could not reach the API)
#             4 timeout · 6 usage limit hit · 10 could not prepare the worktree
#             11 issue refused by the provenance gate (untrusted author or commenter)
#             12 skipped: the ticket already has an open PR, or its worktree already exists
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
TIMEOUT_MIN="${LOOP_TICKET_TIMEOUT_MIN:-90}"
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

# ── Provenance gate: only maintainer-written issue text may become the worker's task ───────────
# This is the enforcement point, not next-ticket.sh: the picker merely *selects*, whereas the
# untrusted text reaches the LLM here. Explicit ticket numbers (backlog-loop.sh 123) and direct
# invocations skip the picker entirely, so the gate has to live in front of the session (ADR-0026).
trust_rc=0
"$REPO_ROOT/scripts/claude/issue-trust.sh" "$ISSUE" || trust_rc=$?
if [ "$trust_rc" -eq 1 ]; then
    meta outcome untrusted
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

# ── Never start a ticket that is already in flight ─────────────────────────────────────────────
# A ticket with an open PR is waiting for the owner's review, and an existing worktree means a
# manual `/ticket` session or an earlier run is still on it. Working it again would open a second
# PR for the same ticket, or fight over the same branch.
open_pr="$(gh pr list --state open --limit 200 --json number,headRefName,isCrossRepository \
    --jq "[.[] | select((.isCrossRepository | not) and (.headRefName | startswith(\"$ISSUE-\"))) | .number] | first // empty")" || {
    meta outcome error
    log "could not list open pull requests — treating as an error"
    exit 3
}
if [ -n "$open_pr" ]; then
    meta outcome skipped
    meta pr "$open_pr"
    log "SKIPPED — PR #$open_pr is already open for this ticket and waits for your review"
    exit 12
fi
# A worktree folder deleted by hand stays registered, and `git worktree add` then refuses the path.
git worktree prune 2>/dev/null || true
if [ -e "$WT" ]; then
    meta outcome skipped
    log "SKIPPED — $WT already exists: a session may be on this ticket. If none is, remove it with: git worktree remove \"$WT\""
    exit 12
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
    if [ "$KEEP_WORKTREE" = "1" ]; then
        log "worktree kept: $WT"
        return 0
    fi
    if git worktree remove "$WT" 2>/dev/null; then
        remove_e2e_images
    else
        log "worktree left in place (not clean): $WT"
    fi
}

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
# on every way out. `pid` is cleared once the session is reaped, so a trap can never hit a
# recycled PID.
pid=""
sleeper=""

stop_session() {
    [ -n "$pid" ] || return 0
    end_session_tree "$pid" 20
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

# ── A fresh worktree from origin/main ──────────────────────────────────────────────────────────
# Detached on purpose: the worker creates the ticket branch itself (`gh issue develop`). Several
# runs fetch at once, and a fetch that loses the ref lock to another one succeeds on a retry.
if ! git fetch --quiet origin main 2>/dev/null; then
    sleep 5
    git fetch --quiet origin main || { meta outcome no-worktree; log "could not fetch origin/main"; exit 10; }
fi
if ! git worktree add --quiet --detach "$WT" origin/main; then
    meta outcome no-worktree
    log "could not create the worktree $WT"
    exit 10
fi

# ── One fresh headless session for the whole ticket ────────────────────────────────────────────
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

# nudge <minutes left> — the prompt of a resume.
nudge() {
    echo "Your last message has no STATUS line, so the loop cannot tell how ticket #$ISSUE ended. \
This is a headless run: when you end your turn, the process exits and nothing wakes you again. \
Background runs are switched off here. If you were waiting for something, such as the test suite, \
run it again now in the foreground, with a Bash timeout long enough for it (at most $BASH_MAX_TIMEOUT_MS ms). \
The loop stops this session in about $1 minutes. \
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
        cd "$WT" || exit 1
        group="$(exec sh -c 'echo "$PPID"')"
        (
            trap '' TERM
            while kill -0 "$$" 2>/dev/null; do sleep 5; done
            end_session_tree "$group" 15
        ) < /dev/null > /dev/null 2>&1 &
        exec "${cmd[@]}"
    ) < /dev/null > "$out" 2>> "$ERR" &
    pid=$!
    set +m
    while kill -0 "$pid" 2>/dev/null; do
        # A background sleep + wait, so a stop signal runs its trap at once instead of after the nap.
        sleep 30 &
        sleeper=$!
        wait "$sleeper"
        sleeper=""
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

log "fresh headless session starting in $WT (model=$MODEL, effort=$EFFORT, timeout=${TIMEOUT_MIN}m)"
start_epoch="$(date +%s)"
# A usage-limit retry runs the ticket again in the same run folder: start from clean logs.
: > "$ERR"
rm -f "$OUT".before-resume-* "$OUT".resume-*
meta resumes 0
run_session "$OUT" "/work-ticket $ISSUE"

# ── A session that stopped without a verdict is resumed ────────────────────────────────────────
# A session that ended normally (no crash, no usage limit) but printed no STATUS line has usually
# stopped to wait for something. Its commits are in the worktree, so a short prompt to the same
# session is cheaper than losing the ticket. A resume writes its own file and replaces $OUT only
# when it ends: a resume that is killed leaves the last result, and its cost, in place. Earlier
# results move to a name the conductor's cost total does not read, because a resumed run reports
# the cost of the whole session (checked against the CLI; --max-budget-usd counts it too).
resumes=0
turns=0
while :; do
    run_turns="$(jq -r '.num_turns // 0' "$OUT" 2>/dev/null || echo 0)"
    case "$run_turns" in ''|*[!0-9]*) run_turns=0 ;; esac
    turns=$((turns + run_turns))
    [ "$claude_rc" -eq 0 ] || break
    [ "$(jq -r '.is_error // false' "$OUT" 2>/dev/null || echo true)" = "false" ] || break
    [ "$(jq -r '.api_error_status // 0' "$OUT" 2>/dev/null || echo 0)" != "429" ] || break
    run_result="$(jq -r '.result // ""' "$OUT" 2>/dev/null || true)"
    ! grep -qE '^STATUS:[[:space:]]*(DONE|BLOCKED)' <<< "$run_result" || break
    [ "$resumes" -lt "$MAX_RESUMES" ] || break
    session_id="$(jq -r '.session_id // ""' "$OUT" 2>/dev/null || true)"
    [ -n "$session_id" ] || break
    left_min=$(( TIMEOUT_MIN - ( $(date +%s) - start_epoch ) / 60 ))
    if [ "$left_min" -lt "$MIN_RESUME_MIN" ]; then
        log "the session stopped without a STATUS line, and only ${left_min}m of the ticket's clock is left — not resuming"
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
    log "the session stopped without a STATUS line — resuming it ($resumes of $MAX_RESUMES)"
    run_session "$OUT.resume-$resumes" "$(nudge "$left_min")" --resume "$session_id"
    # Copy, then rename over: a stop between the two steps still leaves a result in $OUT.
    cp "$OUT" "$OUT.before-resume-$resumes"
    mv "$OUT.resume-$resumes" "$OUT"
done

elapsed_min=$(( ( $(date +%s) - start_epoch ) / 60 ))

result="$(jq -r '.result // ""' "$OUT" 2>/dev/null || echo "")"
is_error="$(jq -r '.is_error // false' "$OUT" 2>/dev/null || echo "true")"
cost="$(jq -r '.total_cost_usd // 0' "$OUT" 2>/dev/null || echo 0)"
meta cost "$cost"
meta turns "$turns"
meta minutes "$elapsed_min"

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
    finish
    log "USAGE LIMIT hit — the conductor will sleep and retry"
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

# ── DONE: verify the PR really exists (never trust a summary alone) ────────────────────────────
# The number in the final message is only a hint: it must be an open PR in this repo whose branch
# belongs to this ticket, or the run is an error. A made-up link, or a link to another PR that the
# summary happens to mention first, must not count as this ticket's PR.
pr_url="$(echo "$result" | grep -oE 'https://github\.com/[^ )>,]+/pull/[0-9]+' | head -1 || true)"
pr_num=""
if [ -n "$pr_url" ]; then
    pr_num="${pr_url##*/}"
else
    pr_num="$(gh pr list --state open --limit 200 --json number,headRefName,isCrossRepository \
        --jq ".[] | select((.isCrossRepository | not) and (.headRefName | startswith(\"$ISSUE-\"))) | .number" \
        | head -1 || true)"
fi
if [ -z "$pr_num" ]; then
    meta outcome error
    finish
    log "worker reported DONE but no PR found — treating as error"
    exit 3
fi
pr_state="$(gh pr view "$pr_num" --json state,isCrossRepository,headRefName \
    --jq '"\(.state) \(.isCrossRepository) \(.headRefName)"' 2>/dev/null || true)"
case "$pr_state" in
    "OPEN false $ISSUE-"*) ;;
    *)
        meta outcome error
        finish
        log "worker reported PR #$pr_num, but that is not an open PR for this ticket (${pr_state:-unreadable}) — treating as error"
        exit 3
        ;;
esac
meta pr "$pr_num"
meta outcome pr-opened
finish
log "PR #$pr_num opened — waiting for your review (cost \$$cost, $turns turns, ${elapsed_min}m, $resumes resume(s))"
exit 0
