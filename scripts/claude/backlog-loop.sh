#!/usr/bin/env bash
# The CONDUCTOR — runs tickets in fresh headless Claude sessions, each in its own git worktree cut
# from origin/main, up to `-j` at a time. Loop control is deterministic bash (zero tokens), so no
# context ever balloons no matter how many tickets run.
#
# It stops at the pull request. Nothing here merges (ADR-0060): you review each PR, approve it by
# assigning yourself, and merge the approved batch with /merge-train.
#
#   work-ticket.sh <n> (worktree → session → PR)  ×  up to -j at once  →  roll-up table of PRs
#
# After a usage limit it waits for the running tickets, naps, and runs the limited tickets again;
# work-ticket.sh then resumes each one's session in the worktree it kept (#934).
#
# Usage:
#   scripts/claude/backlog-loop.sh 123 130 131      # exactly these tickets (the normal use)
#   scripts/claude/backlog-loop.sh -j 1 123 130     # one at a time
#   scripts/claude/backlog-loop.sh -n 3             # the next 3 ready tickets from the picker
#   scripts/claude/backlog-loop.sh                  # every ready ticket (drain)
#   caffeinate -is scripts/claude/backlog-loop.sh 123 130   # keep macOS awake for the run
#
# Env (all forwarded to work-ticket.sh): LOOP_EFFORT and LOOP_MODEL (defaults come from the
#   worker role in ~/.claude/model-policy.env when present, else high/opus),
#   LOOP_CONFIG_DIR (default ~/.claude-account1 — which account runs the loop),
#   LOOP_PERMISSION_MODE (default auto), LOOP_UNSAFE, LOOP_MAX_BUDGET_USD,
#   LOOP_TICKET_TIMEOUT_MIN, LOOP_MAX_RESUMES, LOOP_KEEP_WORKTREE, LOOP_SKIP_LABELS,
#   BASH_MAX_TIMEOUT_MS / BASH_DEFAULT_TIMEOUT_MS (the worker's Bash timeouts),
#   LOOP_TRUSTED_ASSOCIATIONS / LOOP_TRUSTED_LOGINS / LOOP_TRUST_GATE (the provenance gate —
#   ADR-0026; it also fires on explicitly-named tickets, which never touch the picker).
#   Loop-only: LOOP_PARALLEL (default 3, same as -j), LOOP_ALLOW_LOCAL_SCRIPTS=1 (run even when
#   scripts/claude/ here differs from origin/main), LOOP_LIMIT_SLEEP_MIN (default 60),
#   LOOP_LIMIT_RETRIES (default 8 — a limit hit at the start of a 5h usage window needs up to
#   ~5h of naps to outlive it), LOOP_MAX_CONSECUTIVE_FAILURES (default 2).
#
# Raw per-ticket session logs land in logs/claude-loop/<timestamp>/ (debugging only);
# blocked-ticket triage is on GitHub: `gh issue list --label loop-blocked`.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$REPO_ROOT"
SCRIPTS="$REPO_ROOT/scripts/claude"
MAIN_ROOT="$(dirname "$(git rev-parse --path-format=absolute --git-common-dir)")"

MAX=0
PARALLEL="${LOOP_PARALLEL:-3}"
# bash 3.2 (macOS) treats "${arr[@]}" on an empty array as unbound under `set -u`, so the queues
# here are space-separated strings, not arrays.
QUEUE=""
EXPLICIT_MODE=0
while [ $# -gt 0 ]; do
    case "$1" in
        -n) MAX="${2:?-n needs a number}"; shift 2 ;;
        -j) PARALLEL="${2:?-j needs a number}"; shift 2 ;;
        -h|--help) sed -n '2,/^set /p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
        [0-9]*)
            # A number given twice would start two workers racing for one worktree.
            case " $QUEUE " in *" $1 "*) ;; *) QUEUE="$QUEUE $1" ;; esac
            EXPLICIT_MODE=1; shift ;;
        *) echo "unknown argument: $1" >&2; exit 1 ;;
    esac
done
case "$PARALLEL" in
    ''|*[!0-9]*|0) echo "-j needs a positive number, got '$PARALLEL'" >&2; exit 1 ;;
esac

# The loop scripts run from THIS checkout, while every worker session reads its /work-ticket prompt
# from origin/main. A checkout on an old branch would run old loop code, and before ADR-0060 that
# code merged PRs by itself. So the loop starts only when scripts/claude/ matches origin/main.
if [ "${LOOP_ALLOW_LOCAL_SCRIPTS:-0}" != "1" ]; then
    git fetch --quiet origin main 2>/dev/null || true
    if ! git diff --quiet origin/main -- scripts/claude/ 2>/dev/null; then
        echo "scripts/claude/ in $REPO_ROOT differs from origin/main — refusing to run old or unreviewed loop code." >&2
        echo "Run the loop from a checkout that is up to date with main, or set LOOP_ALLOW_LOCAL_SCRIPTS=1 on purpose." >&2
        exit 1
    fi
fi

LIMIT_SLEEP_MIN="${LOOP_LIMIT_SLEEP_MIN:-60}"
LIMIT_RETRIES="${LOOP_LIMIT_RETRIES:-8}"
MAX_FAILURES="${LOOP_MAX_CONSECUTIVE_FAILURES:-2}"

notify() {
    command -v osascript >/dev/null 2>&1 && osascript -e \
        "display notification \"$2\" with title \"$1\"" >/dev/null 2>&1 || true
}

# A bare mkdir mutex is not enough. A conductor killed without running its EXIT trap (terminal
# closed, SIGKILL, power loss) leaves the directory behind, and every later run then refuses to
# start — for a scheduled overnight run, silently into a log nobody reads. So the lock records its
# owner and a lock whose owner is gone is reclaimed instead of blocking the loop forever.
LOCK="$MAIN_ROOT/.claude/backlog-loop.lock"
LOCK_OWNER="$LOCK/pid"
# Without its parent the mkdir below fails as if another conductor held the lock.
mkdir -p "$(dirname "$LOCK")"

lock_owner_alive() {
    local pid
    pid="$(cat "$LOCK_OWNER" 2>/dev/null || true)"
    [ -n "$pid" ] || return 1
    kill -0 "$pid" 2>/dev/null || return 1
    # The PID may have been recycled by an unrelated process — only a live conductor counts.
    ps -p "$pid" -o command= 2>/dev/null | grep -q 'backlog-loop.sh'
}

if ! mkdir "$LOCK" 2>/dev/null; then
    if lock_owner_alive; then
        message="another loop is running (pid $(cat "$LOCK_OWNER" 2>/dev/null)) — refusing to start a second one"
        echo "$message" >&2
        notify "Claude backlog loop did NOT start" "$message"
        exit 1
    fi
    echo "[conductor] stale lock (owner gone) — reclaiming $LOCK" >&2
    # mv is atomic, so if two conductors race to reclaim, only one wins and the loser's mkdir fails.
    if mv "$LOCK" "$LOCK.stale.$$" 2>/dev/null; then
        rm -rf "$LOCK.stale.$$"
    fi
    if ! mkdir "$LOCK" 2>/dev/null; then
        message="lost the race to reclaim the stale lock — another conductor got there first"
        echo "$message" >&2
        notify "Claude backlog loop did NOT start" "$message"
        exit 1
    fi
fi
echo "$$" > "$LOCK_OWNER"

# Running workers as "pid:ticket" pairs. A background child of a non-interactive shell ignores
# SIGINT, so Ctrl-C on the conductor would leave every worker running: they are sent TERM on every
# way out, and each worker kills its own claude session in turn.
RUNNING=""

# Bash reaps a finished background job at once, not at `wait`. From then on the system may give its
# number to another program (#992), and no process group keeps the number reserved: a worker does
# not lead one. So a number that answers `kill -0` proves nothing. This reads the shell's own job
# list instead: bash takes a job off the running list in the same step in which it reaps it. The
# worker does the same for its session (#983). Job control is off here, so bash never sees a job
# stop, and a job is off that list only once it has ended.
job_running() {
    local running
    [ -n "$1" ] || return 1
    running="$(jobs -rp)"
    case $'\n'"$running"$'\n' in *$'\n'"$1"$'\n'*) return 0 ;; esac
    return 1
}

stop_workers() {
    local entry pid
    for entry in $RUNNING; do
        pid="${entry%%:*}"
        if job_running "$pid"; then
            kill "$pid" 2>/dev/null || true
        fi
    done
}

# Every wait here is a background sleep + `wait`, so a stop signal runs its trap at once instead
# of after the nap (a usage-limit nap is an hour).
SLEEPER=""
nap() {
    sleep "$1" &
    SLEEPER=$!
    wait "$SLEEPER" 2>/dev/null || true
    SLEEPER=""
}

# Errexit is on inside the EXIT trap too, so a failed kill must not skip the lock's removal.
stop_nap() {
    if job_running "$SLEEPER"; then
        kill "$SLEEPER" 2>/dev/null || true
    fi
    SLEEPER=""
}
trap 'stop_workers; stop_nap; rm -rf "$LOCK"' EXIT
trap 'exit 130' INT TERM HUP

RUN_DIR="$MAIN_ROOT/logs/claude-loop/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$RUN_DIR"

gh label create loop-blocked --color e36209 \
    --description "claude-loop: needs human input" --force >/dev/null 2>&1 || true

RETRY=""          # tickets that hit the usage limit; they go first once the nap is over
ATTEMPTED=""      # everything dispatched, so the picker never returns it again
opened=0 blocked=0 failed=0 skipped=0 untrusted=0 count=0
consecutive_failures=0
limit_naps=0
limit_hold=0      # 1 = a worker hit the usage limit: dispatch nothing until the running ones end
stop=0            # 1 = dispatch nothing more; finish what runs and report
stop_reason=""
exit_code=0
picker_empty=0

running_count() {
    local entry n=0
    for entry in $RUNNING; do n=$((n + 1)); done
    echo "$n"
}

# First word of a space-separated list, and the list without it.
head_of() { set -- $1; echo "${1:-}"; }
tail_of() { set -- $1; [ $# -gt 0 ] && shift; echo "$*"; }

# Sets NEXT to the next ticket to start, or to nothing. Retries first, then the explicit list or
# the picker. It sets a global instead of printing: `$(next_ticket)` would run in a subshell and
# lose every change it makes to the queues.
NEXT=""
next_ticket() {
    NEXT=""
    if [ -n "${RETRY// /}" ]; then
        NEXT="$(head_of "$RETRY")"; RETRY="$(tail_of "$RETRY")"
        return 0
    fi
    if [ "$MAX" -gt 0 ] && [ "$count" -ge "$MAX" ]; then return 0; fi
    if [ "$EXPLICIT_MODE" -eq 1 ]; then
        NEXT="$(head_of "$QUEUE")"; QUEUE="$(tail_of "$QUEUE")"
        return 0
    fi
    [ "$picker_empty" -eq 1 ] && return 0
    NEXT="$("$SCRIPTS/next-ticket.sh" --exclude "$ATTEMPTED" || true)"
}

dispatch() {
    local ticket="$1" first_time="$2"
    if [ "$first_time" = "1" ]; then
        count=$((count + 1))
        ATTEMPTED="$ATTEMPTED $ticket"
    fi
    echo "[conductor] ── start #$ticket ($(( $(running_count) + 1 ))/$PARALLEL running) ──"
    "$SCRIPTS/work-ticket.sh" "$ticket" "$RUN_DIR" &
    RUNNING="$RUNNING $!:$ticket"
}

handle_exit() {
    local ticket="$1" rc="$2"
    case "$rc" in
        0) opened=$((opened + 1)); consecutive_failures=0 ;;
        2) blocked=$((blocked + 1)); consecutive_failures=0 ;;
        12) skipped=$((skipped + 1)); consecutive_failures=0 ;;
        11)
            # Refused by the provenance gate, at the start (no session ran) or before a resume
            # (the work so far is kept on its branch): the ticket carries untrusted text. Not a
            # systemic failure — skip it and keep going (drain mode never selects one anyway).
            untrusted=$((untrusted + 1)); consecutive_failures=0 ;;
        6)
            RETRY="$RETRY $ticket"
            if [ "$limit_hold" -eq 0 ]; then
                echo "[conductor] usage limit — starting nothing new until the running tickets end"
            fi
            limit_hold=1 ;;
        10)
            stop=1; stop_reason="could not prepare a worktree — see the #$ticket line above"
            exit_code=10 ;;
        *)
            failed=$((failed + 1)); consecutive_failures=$((consecutive_failures + 1))
            if [ "$consecutive_failures" -ge "$MAX_FAILURES" ] && [ "$stop" -eq 0 ]; then
                stop=1; stop_reason="$consecutive_failures consecutive failures — something systemic"
            fi ;;
    esac
}

# A worker that has ended keeps its slot until this check, so handle_exit sees a usage limit or a
# stop before anything new starts in that slot.
reap_finished() {
    local entry pid ticket rc still=""
    for entry in $RUNNING; do
        pid="${entry%%:*}"; ticket="${entry##*:}"
        if job_running "$pid"; then
            still="$still $entry"
            continue
        fi
        rc=0
        wait "$pid" || rc=$?
        handle_exit "$ticket" "$rc"
    done
    RUNNING="$still"
}

echo "[conductor] run $RUN_DIR · up to $PARALLEL at once$([ "$EXPLICIT_MODE" -eq 1 ] && echo " · tickets:$QUEUE")"

while :; do
    reap_finished

    if [ "$stop" -eq 0 ] && [ "$limit_hold" -eq 0 ]; then
        while [ "$(running_count)" -lt "$PARALLEL" ]; do
            is_retry=0
            [ -n "${RETRY// /}" ] && is_retry=1
            next_ticket
            if [ -z "$NEXT" ]; then
                if [ "$EXPLICIT_MODE" -eq 0 ] && [ "$is_retry" -eq 0 ]; then picker_empty=1; fi
                break
            fi
            if [ "$is_retry" -eq 1 ]; then dispatch "$NEXT" 0; else dispatch "$NEXT" 1; fi
            # Give each worker a head start on its fetch and worktree before the next one begins.
            nap 5
        done
    fi

    if [ "$(running_count)" -eq 0 ]; then
        if [ "$stop" -eq 1 ]; then
            break
        fi
        if [ "$limit_hold" -eq 1 ]; then
            limit_naps=$((limit_naps + 1))
            if [ "$limit_naps" -gt "$LIMIT_RETRIES" ]; then
                stop_reason="usage limit persisted after $LIMIT_RETRIES naps"
                break
            fi
            echo "[conductor] usage limit — sleeping ${LIMIT_SLEEP_MIN}m (nap $limit_naps/$LIMIT_RETRIES)"
            nap $(( LIMIT_SLEEP_MIN * 60 ))
            limit_hold=0
            continue
        fi
        # Nothing runs and nothing new started: the list or the picker is empty.
        break
    fi

    nap 10
done

[ -n "$stop_reason" ] && echo "[conductor] stopped early: $stop_reason"

# ── Roll-up: one row per ticket, with the PR state you review next ────────────────────────────
pr_checks() {
    gh pr checks "$1" --json bucket --jq '
        if any(.[]; .bucket == "fail" or .bucket == "cancel") then "red"
        elif any(.[]; .bucket == "pending") then "running"
        else "green" end' 2>/dev/null || echo "?"
}
pr_alerts() {
    gh api "repos/{owner}/{repo}/code-scanning/alerts?ref=refs/pull/$1/merge&state=open&per_page=100" \
        --jq 'length' 2>/dev/null || echo "?"
}

echo
echo "[conductor] ticket   outcome      PR     checks   CodeQL alerts"
total_cost=0
shopt -s nullglob
for meta_file in "$RUN_DIR"/ticket-*.meta; do
    ticket="$(sed -n 's/^issue=//p' "$meta_file" | tail -1)"
    outcome="$(sed -n 's/^outcome=//p' "$meta_file" | tail -1)"
    pr="$(sed -n 's/^pr=//p' "$meta_file" | tail -1)"
    resumes="$(sed -n 's/^resumes=//p' "$meta_file" | tail -1)"
    worktree="$(sed -n 's/^worktree=//p' "$meta_file" | tail -1)"
    checks="-"; alerts="-"
    if [ -n "$pr" ]; then
        checks="$(pr_checks "$pr")"
        alerts="$(pr_alerts "$pr")"
    fi
    # A worker that had to be resumed is worth a look: its session stopped without a STATUS line,
    # said DONE with no PR, or hit the usage limit (#925, #934).
    note=""
    case "$resumes" in ''|0) ;; *) note="  resumed ${resumes}x" ;; esac
    # The limit outlasted every nap: the session waits in its worktree for a later run (#934).
    if [ "$outcome" = "limit" ] && [ "$worktree" = "kept" ]; then
        note="$note  worktree kept: run #$ticket again to resume its session"
    fi
    printf '[conductor] #%-6s %-12s %-6s %-8s %s%s\n' \
        "$ticket" "${outcome:-running?}" "${pr:+#$pr}" "$checks" "$alerts" "$note"
done
ticket_json=( "$RUN_DIR"/ticket-*.json )
shopt -u nullglob
if [ "${#ticket_json[@]}" -gt 0 ]; then
    total_cost="$(jq -s '[.[].total_cost_usd // 0] | add | . * 100 | round / 100' "${ticket_json[@]}" 2>/dev/null || echo 0)"
fi

echo
echo "[conductor] done: $opened PR opened · $blocked blocked · $failed failed · $skipped skipped · $untrusted untrusted · \$$total_cost"
[ "$opened" -gt 0 ] && echo "[conductor] next: review each PR, assign yourself to approve it, then run /merge-train"
[ "$blocked" -gt 0 ] && echo "[conductor] blocked tickets carry your questions as issue comments: gh issue list --label loop-blocked"
echo "[conductor] raw session logs: $RUN_DIR"

notify "Claude backlog loop finished" "$opened PR opened, $blocked blocked, $failed failed (\$$total_cost)"
exit "$exit_code"
