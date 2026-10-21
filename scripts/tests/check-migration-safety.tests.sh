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

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
TMP_ROOT="$(mktemp -d)"
# The unreadable-migration case takes the read bit away; give it back, or rm cannot clean up.
trap 'chmod -R u+rwx "$TMP_ROOT" 2>/dev/null; rm -rf "$TMP_ROOT"' EXIT

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
    # Callers run this inside $(…), where set -e is off: an empty $repo would aim every step below
    # at the root or the current folder (#1042).
    repo="$(mktemp -d "$TMP_ROOT/repo.XXXXXX")" || return 1
    [ -d "$repo" ] || return 1
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
    # The output may hold a file name that is not valid UTF-8, which grep skips in a UTF-8 locale (#1162).
    printf '%s' "$LAST_OUTPUT" | LC_ALL=C grep -qF "$1" \
        || fail "output should contain '$1'" "$LAST_OUTPUT"
}

run_suite() {
    local repo blob migration

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

    # 12. A staged migration deleted only from disk would still be committed, unscanned (#1079).
    repo="$(new_repo)"
    write_migration "$repo" "20260705000012_StagedThenDeleted.cs" \
        'migrationBuilder.DropTable(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    git -C "$repo" add -A
    rm "$repo/src/App.Persistence/Migrations/20260705000012_StagedThenDeleted.cs"
    run_case 2 "a staged migration missing from disk fails instead of passing unscanned" "$repo"
    expect_in_output "20260705000012_StagedThenDeleted.cs"
    expect_in_output "not on disk"

    # 13. The same for a migration committed on the branch and then deleted from disk only.
    repo="$(new_repo)"
    write_migration "$repo" "20260705000013_CommittedThenDeleted.cs" \
        'migrationBuilder.DropTable(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    commit_all "$repo" "destructive migration"
    rm "$repo/src/App.Persistence/Migrations/20260705000013_CommittedThenDeleted.cs"
    run_case 2 "a committed migration missing from disk fails instead of passing unscanned" "$repo" HEAD^1
    expect_in_output "not on disk"

    # 14. The regenerate flow: a branch migration removed with git rm leaves with the next commit,
    #     so only its replacement is scanned.
    repo="$(new_repo)"
    write_migration "$repo" "20260705000014_FirstTry.cs" \
        'migrationBuilder.DropTable(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    commit_all "$repo" "first try"
    git -C "$repo" rm -q "src/App.Persistence/Migrations/20260705000014_FirstTry.cs"
    write_migration "$repo" "20260705000015_SecondTry.cs" \
        'migrationBuilder.AddColumn<string>(name: "N", table: "T");' \
        'migrationBuilder.DropColumn(name: "N", table: "T");'
    run_case 0 "a branch migration removed with git rm is not scanned; its replacement is" "$repo" HEAD^1
    expect_in_output "1 newly-added migration file(s) scanned"

    # 15. git quotes a name with a non-ASCII letter unless core.quotePath is off, and the quoted
    #     name used to fall out of the .cs filter, unscanned (#1079).
    repo="$(new_repo)"
    write_migration "$repo" "20260705000016_UsuńKolumnę.cs" \
        'migrationBuilder.DropColumn(name: "Old", table: "T");' \
        'migrationBuilder.AddColumn<string>(name: "Old", table: "T");'
    commit_all "$repo" "non-ASCII name"
    run_case 1 "a destructive migration with a non-ASCII letter in its name is caught" "$repo" HEAD^1
    expect_in_output "Destructive operation"

    # 16. A migration the guard cannot read fails the run. Root reads any file, so skip it there.
    if [ "$(id -u)" -ne 0 ]; then
        repo="$(new_repo)"
        write_migration "$repo" "20260705000017_Unreadable.cs" \
            'migrationBuilder.DropTable(name: "T");' \
            'migrationBuilder.CreateTable(name: "T");'
        commit_all "$repo" "unreadable"
        chmod 000 "$repo/src/App.Persistence/Migrations/20260705000017_Unreadable.cs"
        run_case 2 "a migration the guard cannot read fails the run" "$repo" HEAD^1
        expect_in_output "could not read"
        chmod 644 "$repo/src/App.Persistence/Migrations/20260705000017_Unreadable.cs"
    else
        printf 'i [%s] running as root, which reads any file — skipped the unreadable-migration case.\n' "$LABEL"
    fi

    # 17. A name that is not valid UTF-8 (é saved as one Latin-1 byte) must stay on the list. In a
    #     UTF-8 locale GNU grep hides such a line, and the guard passed (#1162).
    #     macOS refuses the name on disk, so this case puts it in git only.
    repo="$(new_repo)"
    blob="$(printf '%s\n' 'migrationBuilder.DropTable(name: "T");' | git -C "$repo" hash-object -w --stdin)"
    git -C "$repo" update-index --add --cacheinfo \
        "100644,$blob,src/App.Persistence/Migrations/"$'20260705000018_Caf\351.cs'
    git -C "$repo" commit -qm "migration name that is not valid UTF-8, in git only"
    run_case 2 "a migration whose name is not valid UTF-8 stays on the list of new migrations" "$repo" HEAD^1
    expect_in_output "20260705000018_Caf"

    # 18. The same name on disk is scanned. It runs only where the file system takes the name (Linux).
    if (: > "$TMP_ROOT/"$'20260705000019_Caf\351.cs') 2>/dev/null; then
        repo="$(new_repo)"
        write_migration "$repo" $'20260705000019_Caf\351.cs' \
            'migrationBuilder.DropTable(name: "T");' \
            'migrationBuilder.CreateTable(name: "T");'
        commit_all "$repo" "migration name that is not valid UTF-8"
        if [ "$LABEL" = "sh" ]; then
            run_case 1 "a destructive migration whose name is not valid UTF-8 is caught" "$repo" HEAD^1
            expect_in_output "Destructive operation"
        else
            # .NET cannot open a file whose name is not valid UTF-8, so the twin cannot scan it.
            run_case 2 "a migration whose name is not valid UTF-8 fails the twin instead of passing" "$repo" HEAD^1
            expect_in_output "not valid UTF-8"
        fi
    else
        printf 'i [%s] this file system refuses a name that is not valid UTF-8 — skipped the on-disk case.\n' "$LABEL"
    fi

    # 19. core.quotePath=false still quotes a name that holds a ", a \ or a control character, and the
    #     quoted name fell out of the .cs filter unscanned (#1162).
    repo="$(new_repo)"
    write_migration "$repo" '20260705000020_Drop"Quoted.cs' \
        'migrationBuilder.DropTable(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    commit_all "$repo" "migration name that git quotes"
    run_case 2 "a migration whose name git quotes fails instead of passing unscanned" "$repo" HEAD^1
    expect_in_output "git quotes the name"

    repo="$(new_repo)"
    write_migration "$repo" $'20260705000021_Tab\tName.cs' \
        'migrationBuilder.DropTable(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    run_case 2 "an untracked migration whose name git quotes fails too" "$repo"
    expect_in_output "git quotes the name"

    # 20. C# reads U+2003 and U+00A0 as spaces, and the guard runs in the C locale, where [[:space:]]
    #     is ASCII only (#1162).
    repo="$(new_repo)"
    write_migration "$repo" "20260705000022_UnicodeSpaceCall.cs" \
        $'migrationBuilder.DropTable\342\200\203(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    commit_all "$repo" "destructive call with U+2003"
    run_case 1 "a destructive call with U+2003 before its parenthesis is caught" "$repo" HEAD^1
    expect_in_output "Destructive operation"

    repo="$(new_repo)"
    write_migration "$repo" "20260705000023_UnicodeSpaceUp.cs" \
        'migrationBuilder.DropTable(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    migration="$repo/src/App.Persistence/Migrations/20260705000023_UnicodeSpaceUp.cs"
    sed "s/void Up(/void"$'\302\240'"Up(/" "$migration" > "$migration.tmp" && mv "$migration.tmp" "$migration"
    LC_ALL=C grep -qF $'void\302\240Up(' "$migration" || fail "fixture: U+00A0 was not put after void"
    commit_all "$repo" "Up() declared with U+00A0"
    run_case 1 "an Up() declared with U+00A0 after void is still scanned" "$repo" HEAD^1
    expect_in_output "Destructive operation"

    # 21. A local function whose name starts with a non-ASCII letter and ends in Down must not end the
    #     scan of Up(): only a real `void Down(` does.
    repo="$(new_repo)"
    write_migration "$repo" "20260705000024_LocalFunction.cs" \
        $'void \303\234Down() { }\n            migrationBuilder.DropTable(name: "T");' \
        'migrationBuilder.CreateTable(name: "T");'
    commit_all "$repo" "local function named ÜDown inside Up()"
    run_case 1 "a local function named ÜDown inside Up() does not end the scan" "$repo" HEAD^1
    expect_in_output "Destructive operation"
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
