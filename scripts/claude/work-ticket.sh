#!/usr/bin/env bash
# Works ONE GitHub ticket in a FRESH headless Claude session, then judges the outcome:
#   claude -p "/work-ticket <n>"  →  STATUS: DONE (PR)  →  wait for pr-verify  →  squash-merge  →  sync main
#                                 →  STATUS: BLOCKED    →  label `loop-blocked` + questions as issue comment
#
# This is the context-safe replacement for in-session ticket subagents: the session dies with the
# ticket, so nothing accumulates anywhere. The conductor (backlog-loop.sh) calls this serially.
#
# Usage: work-ticket.sh <issue-number> [run-dir]
# Env:
#   LOOP_EFFORT             claude effort level (default: the worker effort from
#                           ~/.claude/model-policy.env when present, else high — reviews inside
#                           the session run at high via the code-reviewer agent definition)
#   LOOP_MODEL              model (default: the worker model from ~/.claude/model-policy.env
#                           when present, else opus — Opus 5; switched back from Fable 5
#                           on 2026-08-05)
#   LOOP_CONFIG_DIR         Claude config dir = which account runs the loop
#                           (default: ~/.claude-account1)
#   LOOP_GH_USER            gh account whose token backs the loop's gh write calls — PR merge,
#                           labels, issue comments (default: koniecdev, the repo owner); an
#                           existing GH_TOKEN in the environment wins
#   LOOP_PERMISSION_MODE    default: auto, plus a loop-scoped --allowedTools Bash allowlist
#                           (git/gh/dotnet/scripts — see LOOP_ALLOWED_TOOLS below); this does NOT
#                           widen permissions of your interactive sessions
#   LOOP_ALLOWED_TOOLS      override the loop's Bash allowlist (space-separated rule list)
#   LOOP_UNSAFE=1           use --dangerously-skip-permissions instead (full overnight autonomy)
#   LOOP_MAX_BUDGET_USD     optional per-ticket API budget cap
#   LOOP_TICKET_TIMEOUT_MIN wall-clock kill switch per ticket (default: 90)
#   LOOP_CHECKS_TIMEOUT_MIN how long to wait for pr-verify before queueing auto-merge (default: 30)
#   LOOP_TRUSTED_ASSOCIATIONS / LOOP_TRUSTED_LOGINS / LOOP_TRUST_GATE — see issue-trust.sh
#
# Exit codes: 0 merged · 2 blocked · 3 error (incl. a provenance gate that could not reach the API)
#             4 timeout · 5 checks failed / open CodeQL alerts / merge failed · 6 usage limit hit
#             7 auto-merge queued (checks still running) · 10 dirty working copy
#             11 issue refused by the provenance gate (untrusted author or commenter)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$REPO_ROOT"

ISSUE="${1:?usage: work-ticket.sh <issue-number> [run-dir]}"
case "$ISSUE" in
    ''|*[!0-9]*) echo "work-ticket: not an issue number: '$ISSUE'" >&2; exit 3 ;;
esac
RUN_DIR="${2:-$REPO_ROOT/logs/claude-loop/adhoc-$(date +%Y%m%d-%H%M%S)}"
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
CHECKS_TIMEOUT_MIN="${LOOP_CHECKS_TIMEOUT_MIN:-30}"

OUT="$RUN_DIR/ticket-$ISSUE.json"
ERR="$RUN_DIR/ticket-$ISSUE.stderr"
META="$RUN_DIR/ticket-$ISSUE.meta"

for tool in claude gh jq git; do
    command -v "$tool" >/dev/null 2>&1 || { echo "work-ticket: missing dependency: $tool" >&2; exit 3; }
done

log() { echo "[loop] #$ISSUE $(date +%H:%M:%S) $*"; }

meta() { echo "$1=$2" >> "$META"; }

# ── gh identity: write calls must not depend on the machine's ACTIVE gh account ────────────────
# The merge gate, labels and issue comments need write access, but the active gh account here is
# often the EMU work account, which GitHub bars from writing outside its enterprise ("Enterprise
# Managed User cannot access this content"). Mint the owner's token unless the caller set one.
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
    log "REFUSED by the provenance gate — untrusted writer (see above); no session spawned"
    exit 11
fi
if [ "$trust_rc" -ne 0 ]; then
    # An unreadable API is systemic, not a property of this ticket: report it as a session error
    # so the conductor's circuit breaker stops the run instead of "skipping" the whole backlog.
    log "provenance gate could not verify #$ISSUE (rc=$trust_rc) — treating as an error"
    exit 3
fi

# Commit (never delete, never stash) anything a failed/blocked session left behind: leftovers
# become ordinary named git history on a dedicated `loop-salvage/<issue>-<timestamp>` branch,
# cut from wherever the session got to (so partial commits on the ticket branch stay reachable
# from it too). Then return to main.
salvage() {
    if [ -n "$(git status --porcelain)" ]; then
        salvage_branch="loop-salvage/$ISSUE-$(date +%Y%m%d-%H%M%S)"
        git checkout -b "$salvage_branch" --quiet 2>/dev/null || true
        git add -A >/dev/null 2>&1 || true
        git commit --quiet --no-verify \
            -m "claude-loop: salvage uncommitted work for #$ISSUE" \
            -m "Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>" >/dev/null 2>&1 || true
        log "leftover changes committed on branch $salvage_branch"
    fi
    git checkout main --quiet 2>/dev/null || true
}

# ── Preflight: never build on top of unrelated work ────────────────────────────────────────────
if [ -n "$(git status --porcelain)" ]; then
    log "ABORT: working copy is dirty — commit or stash before running the loop"
    exit 10
fi
git checkout main --quiet
git pull --ff-only --quiet

: > "$META"
meta issue "$ISSUE"

# ── One fresh headless session for the whole ticket ────────────────────────────────────────────
ALLOWED_TOOLS="${LOOP_ALLOWED_TOOLS:-Bash(git:*) Bash(gh:*) Bash(dotnet:*) Bash(scripts/:*) Bash(./scripts/:*)}"

cmd=(claude -p "/work-ticket $ISSUE" --output-format json --model "$MODEL" --effort "$EFFORT")
if [ "${LOOP_UNSAFE:-0}" = "1" ]; then
    cmd+=(--dangerously-skip-permissions)
else
    # shellcheck disable=SC2086
    cmd+=(--permission-mode "$PERMISSION_MODE" --allowedTools $ALLOWED_TOOLS)
fi
[ -n "${LOOP_MAX_BUDGET_USD:-}" ] && cmd+=(--max-budget-usd "$LOOP_MAX_BUDGET_USD")

log "fresh headless session starting (model=$MODEL, effort=$EFFORT, timeout=${TIMEOUT_MIN}m)"
start_epoch="$(date +%s)"

set +e
"${cmd[@]}" > "$OUT" 2> "$ERR" &
pid=$!
while kill -0 "$pid" 2>/dev/null; do
    sleep 30
    if [ $(( $(date +%s) - start_epoch )) -ge $(( TIMEOUT_MIN * 60 )) ]; then
        kill "$pid" 2>/dev/null
        sleep 5
        kill -9 "$pid" 2>/dev/null
        wait "$pid" 2>/dev/null
        set -e
        meta outcome timeout
        salvage
        log "TIMEOUT after ${TIMEOUT_MIN}m — session killed, changes salvaged"
        exit 4
    fi
done
wait "$pid"
claude_rc=$?
set -e

elapsed_min=$(( ( $(date +%s) - start_epoch ) / 60 ))

result="$(jq -r '.result // ""' "$OUT" 2>/dev/null || echo "")"
is_error="$(jq -r '.is_error // false' "$OUT" 2>/dev/null || echo "true")"
cost="$(jq -r '.total_cost_usd // 0' "$OUT" 2>/dev/null || echo 0)"
turns="$(jq -r '.num_turns // 0' "$OUT" 2>/dev/null || echo 0)"
meta cost "$cost"
meta turns "$turns"
meta minutes "$elapsed_min"

# ── Usage-limit / hard-error detection ─────────────────────────────────────────────────────────
# The CLI reports plan/rate limits as api_error_status 429 in the result JSON regardless of the
# message wording ("usage limit", "session limit", …) — trust that first; the wording grep stays
# as the fallback for stderr-only failures where no result JSON was written.
api_error_status="$(jq -r '.api_error_status // 0' "$OUT" 2>/dev/null || echo 0)"
combined="$result $(tail -c 2000 "$ERR" 2>/dev/null || true)"
if [ "$api_error_status" = "429" ] || echo "$combined" | grep -qiE 'usage limit|session limit|rate.?limit|overloaded|quota'; then
    meta outcome limit
    salvage
    log "USAGE LIMIT hit — the conductor will sleep and retry"
    exit 6
fi
if [ "$claude_rc" -ne 0 ] || [ "$is_error" = "true" ]; then
    meta outcome error
    salvage
    log "session ERROR (rc=$claude_rc, is_error=$is_error) — see $ERR"
    exit 3
fi

# ── Judge the worker's final message (STATUS contract from /work-ticket) ───────────────────────
if echo "$result" | grep -qE '^STATUS:[[:space:]]*BLOCKED'; then
    meta outcome blocked
    gh label create loop-blocked --color D93F0B \
        --description "claude-loop: needs human input" --force >/dev/null 2>&1 || true
    gh issue edit "$ISSUE" --add-label loop-blocked >/dev/null 2>&1 || true
    comment="$(printf '🤖 **claude-loop: BLOCKED** — needs your input before the loop retries this ticket.\n\n%s' \
        "$(echo "$result" | head -c 4000)")"
    gh issue comment "$ISSUE" --body "$comment" >/dev/null 2>&1 || true
    salvage
    log "BLOCKED — questions posted on the issue, labeled loop-blocked"
    exit 2
fi

if ! echo "$result" | grep -qE '^STATUS:[[:space:]]*DONE'; then
    meta outcome error
    salvage
    log "no STATUS: DONE/BLOCKED contract in the final message — treating as error (see $OUT)"
    exit 3
fi

# ── DONE: verify the PR really exists (never trust a summary alone) ────────────────────────────
pr_url="$(echo "$result" | grep -oE 'https://github\.com/[^ )>,]+/pull/[0-9]+' | head -1 || true)"
pr_num=""
if [ -n "$pr_url" ]; then
    pr_num="${pr_url##*/}"
else
    pr_num="$(gh pr list --state open --json number,headRefName \
        --jq ".[] | select(.headRefName | startswith(\"$ISSUE-\")) | .number" | head -1 || true)"
fi
if [ -z "$pr_num" ]; then
    meta outcome error
    salvage
    log "worker reported DONE but no PR found — treating as error"
    exit 3
fi
meta pr "$pr_num"

# ── Merge gate: wait for pr-verify, then squash-merge (branches are KEPT — house rule) ─────────
log "PR #$pr_num — waiting for required checks (max ${CHECKS_TIMEOUT_MIN}m)"
checks_start="$(date +%s)"
while :; do
    set +e
    gh pr checks "$pr_num" >/dev/null 2>&1
    crc=$?
    set -e
    if [ "$crc" -eq 0 ]; then
        break
    fi
    checks_elapsed=$(( $(date +%s) - checks_start ))
    if [ "$crc" -ne 8 ] && [ "$checks_elapsed" -gt 180 ]; then
        meta outcome checks-failed
        salvage
        log "checks FAILED on PR #$pr_num — left open for triage"
        exit 5
    fi
    if [ "$checks_elapsed" -ge $(( CHECKS_TIMEOUT_MIN * 60 )) ]; then
        if gh pr merge "$pr_num" --squash --auto >/dev/null 2>&1; then
            meta outcome queued
            salvage
            log "checks still running after ${CHECKS_TIMEOUT_MIN}m — auto-merge queued"
            exit 7
        fi
        meta outcome checks-timeout
        salvage
        log "checks timed out and auto-merge unavailable — PR #$pr_num left open"
        exit 5
    fi
    sleep 45
done

# ── CodeQL gate: every code-scanning finding must be handled BEFORE merge ──────────────────────
# Green `gh pr checks` does NOT cover this — the CodeQL check succeeds even when it uploads
# alerts. Query the PR's open alerts directly and fail CLOSED: an unreadable API refuses to
# merge blind (same philosophy as the provenance gate). Docs-only PRs skip CodeQL and simply
# return an empty list here.
alerts="$(gh api "repos/{owner}/{repo}/code-scanning/alerts?ref=refs/pull/$pr_num/merge&state=open&per_page=100" \
    --jq '[.[] | "- \(.rule.id) (\(.rule.severity)) \(.most_recent_instance.location.path):\(.most_recent_instance.location.start_line)"] | join("\n")' \
    2>/dev/null)" || {
    meta outcome codeql-unverifiable
    salvage
    log "could not read code-scanning alerts for PR #$pr_num — refusing to merge blind"
    exit 5
}
if [ -n "$alerts" ]; then
    meta outcome codeql-alerts
    gh pr comment "$pr_num" --body "$(printf '🤖 **claude-loop: merge refused — open CodeQL alerts on this PR.** Fix each one (or dismiss it with a stated reason) before merging:\n\n%s' "$alerts")" >/dev/null 2>&1 || true
    salvage
    log "open CodeQL alerts on PR #$pr_num — merge refused, PR left open for triage"
    exit 5
fi

if ! gh pr merge "$pr_num" --squash; then
    meta outcome merge-failed
    salvage
    log "merge FAILED on PR #$pr_num — left open for triage"
    exit 5
fi
git checkout main --quiet
git pull --ff-only --quiet
meta outcome merged
log "MERGED PR #$pr_num (cost \$$cost, $turns turns, ${elapsed_min}m)"
exit 0
