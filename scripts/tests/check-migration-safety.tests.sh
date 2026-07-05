#!/usr/bin/env bash
# Test suite for the migration-safety guard (MIGR-03, ADR-0023 §4).
#
# Each case builds a throwaway git repo whose initial commit already contains a
# DESTRUCTIVE legacy migration — proving shipped migrations are never re-flagged —
# then adds fixture migrations and asserts the guard's exit code and output.
# CI runs this right before the guard itself, so the gate cannot rot silently.
# When pwsh is available (it is on the ubuntu runners), the whole suite re-runs
# against the check-migration-safety.ps1 twin, keeping the two in sync mechanically.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

# Isolate from the developer's git config (gpg signing, hooks, defaults).
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=migration-safety-tests GIT_AUTHOR_EMAIL=tests@localhost
export GIT_COMMITTER_NAME=migration-safety-tests GIT_COMMITTER_EMAIL=tests@localhost

CHECKER=""
LABEL=""
LAST_OUTPUT=""
cases=0

fail() {
    printf '✗ [%s] %s\n' "$LABEL" "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    exit 1
}

write_migration() {
    # $1 repo, $2 file name, $3 Up() body, $4 Down() body
    local dir="$1/src/App.Persistence/Migrations"
    mkdir -p "$dir"
    cat > "$dir/$2" <<EOF
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Persistence.Migrations
{
    public partial class Fixture : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            $3
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            $4
        }
    }
}
EOF
}

new_repo() {
    local repo
    repo="$(mktemp -d "$TMP_ROOT/repo.XXXXXX")"
    git -C "$repo" init -q -b main
    write_migration "$repo" "20260101000000_LegacyDropColumn.cs" \
        'migrationBuilder.DropColumn(name: "Old", table: "Legacy");' \
        'migrationBuilder.AddColumn<string>(name: "Old", table: "Legacy");'
    git -C "$repo" add -A
    git -C "$repo" commit -qm "initial schema (destructive legacy migration, unacknowledged)"
    printf '%s' "$repo"
}

commit_all() {
    git -C "$1" add -A
    git -C "$1" commit -qm "$2"
}

run_case() {
    # $1 expected exit code, $2 description, $3 repo; remaining args go to the checker
    local expected="$1" desc="$2" repo="$3"
    shift 3
    local rc=0
    LAST_OUTPUT="$( (cd "$repo" && "$CHECKER" "$@") 2>&1 )" || rc=$?
    if [ "$rc" -ne "$expected" ]; then
        fail "$desc — expected exit $expected, got $rc" "$LAST_OUTPUT"
    fi
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$LABEL" "$desc"
}

expect_in_output() {
    printf '%s' "$LAST_OUTPUT" | grep -qF "$1" \
        || fail "output should contain '$1'" "$LAST_OUTPUT"
}

run_suite() {
    local repo

    # 1. Additive migration passes — and the destructive legacy file is not re-flagged.
    repo="$(new_repo)"
    write_migration "$repo" "20260705000001_AddThing.cs" \
        'migrationBuilder.AddColumn<string>(name: "New", table: "T", nullable: true);' \
        'migrationBuilder.DropColumn(name: "New", table: "T");'
    commit_all "$repo" "additive"
    run_case 0 "additive migration passes; legacy destructive file stays unflagged" "$repo" HEAD^1
    expect_in_output "1 newly-added migration file(s) scanned"

    # 2. Destructive migration fails and names every flagged operation.
    repo="$(new_repo)"
    write_migration "$repo" "20260705000002_DropStuff.cs" \
        'migrationBuilder.DropColumn(name: "Gone", table: "T");
            migrationBuilder.AlterColumn<string>(name: "Kept", table: "T", nullable: false);
            migrationBuilder.RenameTable(name: "T", newName: "T2");' \
        'migrationBuilder.AddColumn<string>(name: "Gone", table: "T");'
    commit_all "$repo" "destructive"
    run_case 1 "destructive migration fails" "$repo" HEAD^1
    expect_in_output "DropColumn"
    expect_in_output "AlterColumn"
    expect_in_output "RenameTable"
    expect_in_output "20260705000002_DropStuff.cs"
    expect_in_output "MIGRATION-SAFETY: acknowledged"

    # 3. The in-file acknowledgment token turns the same failure into a pass.
    repo="$(new_repo)"
    write_migration "$repo" "20260705000003_ContractStep.cs" \
        '// MIGRATION-SAFETY: acknowledged — contract step; the expand release shipped in the previous deploy
            migrationBuilder.DropColumn(name: "Gone", table: "T");' \
        'migrationBuilder.AddColumn<string>(name: "Gone", table: "T");'
    commit_all "$repo" "acknowledged contract step"
    run_case 0 "acknowledged destructive migration passes" "$repo" HEAD^1
    expect_in_output "Acknowledged destructive migration"

    # 4. Destructive ops in Down() only — the shape of every additive EF migration — pass.
    repo="$(new_repo)"
    write_migration "$repo" "20260705000004_CreateTable.cs" \
        'migrationBuilder.CreateTable(name: "Fresh");' \
        'migrationBuilder.DropTable(name: "Fresh");'
    commit_all "$repo" "create table"
    run_case 0 "destructive operations in Down() only are ignored" "$repo" HEAD^1

    # 5. Designer files and model snapshots are excluded even when destructive text matches.
    repo="$(new_repo)"
    write_migration "$repo" "20260705000005_Whatever.Designer.cs" \
        'migrationBuilder.DropTable(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    write_migration "$repo" "AppWriteDbContextModelSnapshot.cs" \
        'migrationBuilder.DropColumn(name: "X", table: "T");' \
        'migrationBuilder.AddColumn<string>(name: "X", table: "T");'
    commit_all "$repo" "designer + snapshot"
    run_case 0 "Designer and ModelSnapshot files are excluded" "$repo" HEAD^1
    expect_in_output "no newly-added migration files"

    # 6. An uncommitted (untracked) destructive migration is caught with no base argument.
    repo="$(new_repo)"
    write_migration "$repo" "20260705000006_UncommittedDrop.cs" \
        'migrationBuilder.DropTable(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    run_case 1 "untracked destructive migration is caught pre-commit" "$repo"
    expect_in_output "20260705000006_UncommittedDrop.cs"

    # 7. An unresolvable explicit base ref is a setup error, not a pass.
    repo="$(new_repo)"
    run_case 2 "unresolvable base ref exits 2" "$repo" no-such-ref

    # 8. Rename pairing must not hide an added migration: deleting one migration and
    #    adding a similar destructive one (the regenerate-a-migration flow) would be
    #    reported as R, not A, without --no-renames.
    repo="$(new_repo)"
    rm "$repo/src/App.Persistence/Migrations/20260101000000_LegacyDropColumn.cs"
    write_migration "$repo" "20260705000008_RegeneratedDrop.cs" \
        'migrationBuilder.DropColumn(name: "Old", table: "Legacy");' \
        'migrationBuilder.AddColumn<string>(name: "Old", table: "Legacy");'
    commit_all "$repo" "regenerated migration"
    run_case 1 "rename-paired regenerated destructive migration is still caught" "$repo" HEAD^1
    expect_in_output "20260705000008_RegeneratedDrop.cs"

    # 9. A staged-but-uncommitted destructive migration is caught (the --cached leg).
    repo="$(new_repo)"
    write_migration "$repo" "20260705000009_StagedDrop.cs" \
        'migrationBuilder.DropTable(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    git -C "$repo" add -A
    run_case 1 "staged destructive migration is caught pre-commit" "$repo"
    expect_in_output "20260705000009_StagedDrop.cs"

    # 10. No base resolvable at all (no origin/main, no main): warn, still scan
    #     staged + untracked instead of passing silently.
    repo="$(new_repo)"
    git -C "$repo" branch -m main trunk
    write_migration "$repo" "20260705000010_NoBaseDrop.cs" \
        'migrationBuilder.DropTable(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    run_case 1 "without any resolvable base the untracked scan still catches" "$repo"
    expect_in_output "scanning only staged and untracked migration files"
    expect_in_output "20260705000010_NoBaseDrop.cs"

    # 11. Nested generic type arguments must not defeat the regex.
    repo="$(new_repo)"
    write_migration "$repo" "20260705000011_NestedGeneric.cs" \
        'migrationBuilder.AlterColumn<Dictionary<string, string>>(name: "Data", table: "T", nullable: false);' \
        'migrationBuilder.AlterColumn<Dictionary<string, string>>(name: "Data", table: "T", nullable: true);'
    commit_all "$repo" "nested generic alter"
    run_case 1 "AlterColumn with nested generic type arguments is caught" "$repo" HEAD^1
    expect_in_output "20260705000011_NestedGeneric.cs"
}

CHECKER="$SCRIPTS_DIR/check-migration-safety.sh"
LABEL="sh"
run_suite

if command -v pwsh >/dev/null 2>&1; then
    ps1_runner="$TMP_ROOT/run-ps1.sh"
    printf '#!/usr/bin/env bash\nexec pwsh -NoProfile -File "%s" "$@"\n' \
        "$SCRIPTS_DIR/check-migration-safety.ps1" > "$ps1_runner"
    chmod +x "$ps1_runner"
    CHECKER="$ps1_runner"
    LABEL="ps1"
    run_suite
else
    printf 'i pwsh not found — skipped the check-migration-safety.ps1 twin suite.\n'
fi

printf 'All %d migration-safety guard case(s) passed.\n' "$cases"
