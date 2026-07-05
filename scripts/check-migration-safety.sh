#!/usr/bin/env bash
# Migration-safety guard (MIGR-03) — the schema analog of check-ssr-purity.sh.
#
# ADR-0023: migrations are forward-only and N-1 backward-compatible — the previous app
# revision keeps serving on the new schema during every deploy's smoke window and after
# any failed rollout (rollback moves traffic, never schema). A destructive migration
# turns that window into an outage, so it must be a deliberate expand → backfill →
# contract step, never an accident that passes a green gate.
#
# This is a pure text scan (no SDK — it runs before setup-dotnet, in the exact slot
# check-ssr-purity.sh occupies in ci.yml and pr-verify.yml). It looks at migration
# files NEWLY ADDED relative to a base ref (CI passes HEAD^1; locally it defaults to
# the merge-base with origin/main, plus staged and untracked files so it works
# pre-commit). Only the Up() body is scanned — every Down() contains drops by
# construction and never runs in shared environments (ADR-0023 §1). Designer files and
# model snapshots are excluded. Already-shipped migrations are never re-flagged.
#
# Escape hatch (ADR-0023 §4): a deliberate destructive step carries a comment line
# containing the token `MIGRATION-SAFETY: acknowledged` followed by the reason —
# e.g.  // MIGRATION-SAFETY: acknowledged — contract step of #123; expand shipped in #120
# A flagged file carrying the token passes.
#
# Accepted false negatives (ADR-0023 trade-offs): destructive raw migrationBuilder.Sql(),
# and DDL edited into an ALREADY-MERGED migration file (modified, not added). Squawk over
# `dotnet ef migrations script` is the recorded upgrade path if this regex proves too
# coarse. AlterColumn always flags — type changes are destructive regardless of
# nullability (ADR-0023 §3); a benign widening costs one acknowledgment line.
#
# Usage: check-migration-safety.sh [<base-ref>]
# Exit codes: 0 = clean, 1 = unacknowledged destructive migration, 2 = cannot run.
# Tests: scripts/tests/check-migration-safety.tests.sh (CI runs them right before this
# guard). Keep this in sync with its check-migration-safety.ps1 twin.

set -euo pipefail

# The generic-args group is [^(] (not [^>]) so nested generics still match:
# AlterColumn<Dictionary<string, string>>(. The trailing paren is a character
# class, not \( — awk -v applies C-escape processing to the pattern, and BSD
# awk turns \( into a bare group-opener.
DESTRUCTIVE='(DropColumn|DropTable|RenameColumn|RenameTable|DropIndex|AlterColumn)[[:space:]]*(<[^(]*>)?[[:space:]]*[(]'
ACKNOWLEDGED='MIGRATION-SAFETY:[[:space:]]*acknowledged'

if ! repo_root="$(git rev-parse --show-toplevel 2>/dev/null)"; then
    echo "Migration-safety guard: not inside a git repository." >&2
    exit 2
fi
cd "$repo_root"

base="${1:-}"
if [ -n "$base" ]; then
    if ! git rev-parse --quiet --verify "$base^{commit}" >/dev/null; then
        echo "Migration-safety guard: cannot resolve base ref '$base'." >&2
        exit 2
    fi
else
    for candidate in origin/main main; do
        if git rev-parse --quiet --verify "$candidate^{commit}" >/dev/null; then
            base="$candidate"
            break
        fi
    done
    if [ -z "$base" ]; then
        echo "Migration-safety guard: no base ref given and neither origin/main nor main exists;" >&2
        echo "scanning only staged and untracked migration files." >&2
    fi
fi

# Newly-added migration files = added vs base (merge-base semantics via A...HEAD)
# + staged-new + untracked, so the guard is useful before anything is committed.
# --no-renames: default rename detection pairs a deleted migration with a similar
# new one (the regenerate-a-migration flow) and hides the addition from
# --diff-filter=A. Each git leg fails CLOSED (exit 2) — a gate that cannot compute
# its input must not pass. Designer files and model snapshots restate the model,
# not the DDL — excluded.
added_committed=""
if [ -n "$base" ]; then
    if ! added_committed="$(git diff --no-renames --name-only --diff-filter=A "$base...HEAD" --)"; then
        echo "Migration-safety guard: git diff against base '$base' failed." >&2
        exit 2
    fi
fi
if ! added_staged="$(git diff --no-renames --cached --name-only --diff-filter=A --)"; then
    echo "Migration-safety guard: git diff --cached failed." >&2
    exit 2
fi
if ! untracked="$(git ls-files --others --exclude-standard)"; then
    echo "Migration-safety guard: git ls-files failed." >&2
    exit 2
fi

new_files="$(
    printf '%s\n%s\n%s\n' "$added_committed" "$added_staged" "$untracked" \
        | grep -E '(^|/)Migrations/[^/]+\.cs$' \
        | grep -vE '(\.Designer\.cs|ModelSnapshot\.cs)$' \
        | sort -u || true
)"

if [ -z "$new_files" ]; then
    echo "✓ Migration-safety guard passed — no newly-added migration files."
    exit 0
fi

fail=0
scanned=0

while IFS= read -r file; do
    [ -f "$file" ] || continue
    scanned=$((scanned + 1))

    # Only the Up() body: lines from `void Up(` up to (excluding) `void Down(` or EOF.
    hits="$(awk -v pattern="$DESTRUCTIVE" '
        /void[[:space:]]+Up[[:space:]]*\(/  { in_up = 1 }
        /void[[:space:]]+Down[[:space:]]*\(/ { in_up = 0 }
        in_up && $0 ~ pattern {
            line = $0
            sub(/^[[:space:]]+/, "", line)
            printf "    %s:%d: %s\n", FILENAME, FNR, line
        }
    ' "$file")"
    [ -n "$hits" ] || continue

    if marker="$(grep -nE "$ACKNOWLEDGED" "$file" | head -1 | sed -E 's/^([0-9]+):[[:space:]]*/\1: /')"; then
        echo "✓ Acknowledged destructive migration (deliberate — ADR-0023 §3 step):"
        printf '    %s:%s\n\n' "$file" "$marker"
        continue
    fi

    fail=1
    echo "✗ Destructive operation(s) in a newly-added migration, without acknowledgment:"
    printf '%s\n\n' "$hits"
done <<< "$new_files"

if [ "$fail" -ne 0 ]; then
    echo "──────────────────────────────────────────────────────────────────────"
    echo "Migration-safety guard FAILED — migrations must be N-1 backward-compatible."
    echo "The previous app revision serves on this schema during every deploy (ADR-0023)."
    echo "Split the change: expand → backfill → contract, across ≥ 2 deploys."
    echo "Deliberate contract step (or the dropped shape never shipped)? Add a comment"
    echo "line inside the migration file:"
    echo "    // MIGRATION-SAFETY: acknowledged — <reason>"
    echo "See CLAUDE.md → 'Migrations are forward-only' and docs/adr/0023-*.md."
    exit 1
fi

echo "✓ Migration-safety guard passed — $scanned newly-added migration file(s) scanned."
