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
# Env (all forwarded to work-ticket.sh): LOOP_EFFORT (default max), LOOP_MODEL (default opus),
#   LOOP_CONFIG_DIR (default ~/.claude-account1 — which account runs the loop),
#   LOOP_PERMISSION_MODE (default auto), LOOP_UNSAFE, LOOP_MAX_BUDGET_USD,
#   LOOP_TICKET_TIMEOUT_MIN, LOOP_CHECKS_TIMEOUT_MIN, LOOP_SKIP_LABELS,
#   LOOP_TRUSTED_ASSOCIATIONS / LOOP_TRUSTED_LOGINS / LOOP_TRUST_GATE (the provenance gate —
#   ADR-0026; it also fires on explicitly-named tickets, which never touch the picker).
#   Loop-only: LOOP_LIMIT_SLEEP_MIN (default 60), LOOP_LIMIT_RETRIES (default 4),
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
LIMIT_RETRIES="${LOOP_LIMIT_RETRIES:-4}"
MAX_FAILURES="${LOOP_MAX_CONSECUTIVE_FAILURES:-2}"

LOCK="$REPO_ROOT/.claude/backlog-loop.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
    echo "another loop appears to be running — remove $LOCK if it is stale" >&2
    exit 1
fi
trap 'rm -rf "$LOCK"' EXIT INT TERM

RUN_DIR="$REPO_ROOT/logs/claude-loop/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$RUN_DIR"

gh label create loop-blocked --color D93F0B \
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

command -v osascript >/dev/null 2>&1 && osascript -e \
    "display notification \"$merged merged, $blocked blocked, $failed failed (\$$total_cost)\" with title \"Claude backlog loop finished\"" \
    >/dev/null 2>&1 || true
