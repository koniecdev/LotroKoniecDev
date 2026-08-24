#!/usr/bin/env bash
# The CONDUCTOR — drains READY tickets serially, one FRESH headless Claude session per ticket.
# Replaces the old in-session /backlog orchestrator: loop control is deterministic bash (zero
# tokens), so no context ever balloons no matter how many tickets run overnight.
#
#   next-ticket.sh  →  work-ticket.sh <n>  →  merged? blocked? limit?  →  next…
#
# Usage:
#   scripts/claude/backlog-loop.sh              # drain: run until no ready ticket is left
#   scripts/claude/backlog-loop.sh -n 3         # at most 3 tickets
#   scripts/claude/backlog-loop.sh 123 130      # exactly these tickets, in this order
#   caffeinate -is scripts/claude/backlog-loop.sh   # overnight on macOS (blocks sleep)
#
# Env (all forwarded to work-ticket.sh): LOOP_EFFORT and LOOP_MODEL (defaults come from the
#   worker role in ~/.claude/model-policy.env when present, else high/opus; reviews run at
#   high via the code-reviewer agent definition),
#   LOOP_CONFIG_DIR (default ~/.claude-account1 — which account runs the loop),
#   LOOP_PERMISSION_MODE (default auto), LOOP_UNSAFE, LOOP_MAX_BUDGET_USD,
#   LOOP_TICKET_TIMEOUT_MIN, LOOP_CHECKS_TIMEOUT_MIN, LOOP_SKIP_LABELS,
#   LOOP_TRUSTED_ASSOCIATIONS / LOOP_TRUSTED_LOGINS / LOOP_TRUST_GATE (the provenance gate —
#   ADR-0026; it also fires on explicitly-named tickets, which never touch the picker).
#   Loop-only: LOOP_LIMIT_SLEEP_MIN (default 60), LOOP_LIMIT_RETRIES (default 8 — a limit hit at
#   the start of a 5h usage window needs up to ~5h of naps to outlive it),
#   LOOP_MAX_CONSECUTIVE_FAILURES (default 2).
#
# Raw per-ticket session logs land in logs/claude-loop/<timestamp>/ (debugging only);
# blocked-ticket triage is on GitHub: `gh issue list --label loop-blocked`.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$REPO_ROOT"
SCRIPTS="$REPO_ROOT/scripts/claude"

MAX=0
EXPLICIT_TICKETS=()
while [ $# -gt 0 ]; do
    case "$1" in
        -n) MAX="${2:?-n needs a number}"; shift 2 ;;
        -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        [0-9]*) EXPLICIT_TICKETS+=("$1"); shift ;;
        *) echo "unknown argument: $1" >&2; exit 1 ;;
    esac
done

# Explicit-ticket mode is tracked by a flag + index over a real array — not "is the string
# non-empty". A string list broke two ways: a leading space made the first selection empty
# (nothing ran), and an exhausted list fell through to drain mode (whole backlog got milled).
EXPLICIT_MODE=0
if [ "${#EXPLICIT_TICKETS[@]}" -gt 0 ]; then EXPLICIT_MODE=1; fi
EXPLICIT_INDEX=0

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
LOCK="$REPO_ROOT/.claude/backlog-loop.lock"
LOCK_OWNER="$LOCK/pid"

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
trap 'rm -rf "$LOCK"' EXIT INT TERM HUP

RUN_DIR="$REPO_ROOT/logs/claude-loop/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$RUN_DIR"

gh label create loop-blocked --color e36209 \
    --description "claude-loop: needs human input" --force >/dev/null 2>&1 || true

attempted=""
merged=0 blocked=0 failed=0 queued=0 untrusted=0 count=0
consecutive_failures=0
limit_naps=0

while :; do
    if [ "$MAX" -gt 0 ] && [ "$count" -ge "$MAX" ]; then
        echo "[conductor] ticket budget reached ($MAX)"
        break
    fi

    if [ "$EXPLICIT_MODE" -eq 1 ]; then
        if [ "$EXPLICIT_INDEX" -ge "${#EXPLICIT_TICKETS[@]}" ]; then
            echo "[conductor] explicit ticket list drained — done"
            break
        fi
        next="${EXPLICIT_TICKETS[$EXPLICIT_INDEX]}"
        EXPLICIT_INDEX=$((EXPLICIT_INDEX + 1))
    else
        next="$("$SCRIPTS/next-ticket.sh" --exclude "$attempted" || true)"
        if [ -z "$next" ]; then
            echo "[conductor] no ready ticket left — done"
            break
        fi
    fi

    count=$((count + 1))
    echo "[conductor] ── ticket $count: #$next ──────────────────────────────"

    set +e
    "$SCRIPTS/work-ticket.sh" "$next" "$RUN_DIR"
    rc=$?
    set -e

    case "$rc" in
        6)
            limit_naps=$((limit_naps + 1))
            count=$((count - 1))
            [ "$EXPLICIT_MODE" -eq 1 ] && EXPLICIT_INDEX=$((EXPLICIT_INDEX - 1))
            if [ "$limit_naps" -gt "$LIMIT_RETRIES" ]; then
                echo "[conductor] usage limit persisted after $LIMIT_RETRIES naps — stopping"
                break
            fi
            echo "[conductor] usage limit — sleeping ${LIMIT_SLEEP_MIN}m (nap $limit_naps/$LIMIT_RETRIES)"
            sleep $(( LIMIT_SLEEP_MIN * 60 ))
            continue
            ;;
        0) merged=$((merged + 1)); consecutive_failures=0 ;;
        7) queued=$((queued + 1)); consecutive_failures=0 ;;
        2) blocked=$((blocked + 1)); consecutive_failures=0 ;;
        11)
            # Refused before any session started: the ticket carries untrusted text. Not a
            # systemic failure — skip it and keep draining (drain mode never selects one anyway).
            untrusted=$((untrusted + 1)); consecutive_failures=0
            echo "[conductor] #$next refused by the provenance gate — skipping"
            ;;
        10)
            echo "[conductor] dirty working copy — stopping (nothing was touched)"
            notify "Claude backlog loop stopped" "dirty working copy — nothing was touched"
            exit 10
            ;;
        *) failed=$((failed + 1)); consecutive_failures=$((consecutive_failures + 1)) ;;
    esac

    attempted="$attempted $next"

    if [ "$consecutive_failures" -ge "$MAX_FAILURES" ]; then
        echo "[conductor] $consecutive_failures consecutive failures — something systemic, stopping"
        break
    fi
done

shopt -s nullglob
ticket_json=( "$RUN_DIR"/ticket-*.json )
shopt -u nullglob
if [ "${#ticket_json[@]}" -gt 0 ]; then
    total_cost="$(jq -s '[.[].total_cost_usd // 0] | add | . * 100 | round / 100' "${ticket_json[@]}" 2>/dev/null || echo 0)"
else
    total_cost=0
fi

echo
echo "[conductor] done: $merged merged · $queued auto-merge queued · $blocked blocked · $failed failed · $untrusted untrusted · \$$total_cost"
[ "$blocked" -gt 0 ] && echo "[conductor] blocked tickets carry your questions as issue comments: gh issue list --label loop-blocked"
echo "[conductor] raw session logs: $RUN_DIR"

notify "Claude backlog loop finished" "$merged merged, $blocked blocked, $failed failed (\$$total_cost)"
