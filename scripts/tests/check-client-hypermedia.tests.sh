#!/usr/bin/env bash
# Test suite for the hypermedia guard (check-client-hypermedia.sh / .ps1).
#
# The guard only ever runs against a clean tree in CI, so "it passed" proves it did not
# false-positive — never that it still FIRES, and never that it scanned anything at all (#1079).
# These cases prove both directions on throwaway fixture trees: a hardcoded API path in either
# client fails, a comment that names a route passes, and a run with no file to scan fails with
# exit 2. CI runs this right before the guard itself, so the gate cannot rot silently.
# When pwsh is available (it is on the ubuntu runners), the whole suite re-runs against
# the .ps1 twin, so both twins must give the same exit code on every case.

set -euo pipefail

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
TMP_ROOT="$(mktemp -d)"
# The unreadable-file cases take the read bit away; give it back, or rm cannot clean up.
trap 'chmod -R u+rwx "$TMP_ROOT" 2>/dev/null; rm -rf "$TMP_ROOT"' EXIT

GUARD_SH="$SCRIPTS_DIR/check-client-hypermedia.sh"
GUARD_PS1="$SCRIPTS_DIR/check-client-hypermedia.ps1"
LABEL=""
RUNNER=""
LAST_OUTPUT=""
cases=0

fail() {
    printf '✗ [%s] %s\n' "$LABEL" "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    exit 1
}

# Builds a fixture repo holding a copy of the guard, so the guard resolves src/Frontend and
# src/Patcher relative to itself exactly as it does in the real tree. Each client holds one clean
# source file.
new_tree() {
    local tree
    tree="$(mktemp -d "$TMP_ROOT/tree.XXXXXX")"
    mkdir -p "$tree/scripts" "$tree/src/Frontend/Pages" "$tree/src/Patcher/Cli"
    cp "$GUARD_SH" "$GUARD_PS1" "$tree/scripts/"
    printf '%s\n' '<p>ok</p>' > "$tree/src/Frontend/Pages/Index.razor"
    printf '%s\n' 'namespace Cli;' > "$tree/src/Patcher/Cli/Program.cs"
    printf '%s' "$tree"
}

# $1 = tree, $2 = file path under the tree, $3 = its contents.
write_file() {
    mkdir -p "$(dirname "$1/$2")"
    printf '%s\n' "$3" > "$1/$2"
}

run_tree() {
    # $1 expected exit code, $2 description, $3 fixture tree
    local expected="$1" desc="$2" tree="$3" rc=0
    LAST_OUTPUT="$("$RUNNER" "$tree" 2>&1)" || rc=$?
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
    local tree

    # --- the guard scans something, or it does not pass (#1079) ---
    tree="$(new_tree)"
    rm -rf "$tree/src/Patcher"
    run_tree 2 "a tree without the Patcher folder fails" "$tree"
    expect_in_output "client directory not found"

    tree="$(new_tree)"
    rm -rf "$tree/src/Frontend"
    run_tree 2 "a tree without the Frontend folder fails" "$tree"
    expect_in_output "client directory not found"

    tree="$(new_tree)"
    rm "$tree/src/Patcher/Cli/Program.cs"
    write_file "$tree" src/Patcher/Cli/Cli.csproj '<Project Sdk="Microsoft.NET.Sdk"></Project>'
    run_tree 2 "a Patcher folder with no .razor or .cs file fails" "$tree"
    expect_in_output "nothing was checked"

    tree="$(new_tree)"
    rm "$tree/src/Frontend/Pages/Index.razor"
    write_file "$tree" src/Frontend/obj/Debug/Index.razor.g.cs 'namespace Generated;'
    run_tree 2 "a Frontend folder whose only source is under obj/ fails" "$tree"
    expect_in_output "nothing was checked"

    # Root ignores the read bit, so these cases prove nothing when the suite runs as root.
    if [ "$(id -u)" -ne 0 ]; then
        tree="$(new_tree)"
        mkdir -p "$tree/src/Frontend/Locked"
        chmod 000 "$tree/src/Frontend/Locked"
        run_tree 2 "a Frontend folder the guard cannot read fails the run" "$tree"
        expect_in_output "could not list"
        chmod 755 "$tree/src/Frontend/Locked"

        tree="$(new_tree)"
        write_file "$tree" src/Patcher/Cli/Endpoints.cs 'const string Path = "/api/v1/translation-files";'
        chmod 000 "$tree/src/Patcher/Cli/Endpoints.cs"
        run_tree 2 "a Patcher file the guard cannot read fails the run" "$tree"
        expect_in_output "could not read"
        chmod 644 "$tree/src/Patcher/Cli/Endpoints.cs"
    else
        printf 'i [%s] running as root, which reads any file — skipped the unreadable-file cases.\n' "$LABEL"
    fi

    # --- clean clients pass ---
    tree="$(new_tree)"
    run_tree 0 "clients with no API path pass" "$tree"

    tree="$(new_tree)"
    write_file "$tree" src/Frontend/Pages/Rels.cs '// The translations rel points at GET /api/v1/translations.'
    run_tree 0 "a comment that names a route outside a string literal passes" "$tree"

    tree="$(new_tree)"
    write_file "$tree" src/Frontend/obj/Debug/Routes.g.cs 'const string Route = "/api/v1/translations";'
    run_tree 0 "an API path in generated code under obj/ is not scanned" "$tree"

    # PowerShell on Linux and macOS hides dot-folders unless -Force is given; grep does not.
    tree="$(new_tree)"
    write_file "$tree" src/Frontend/.shared/Paths.cs 'const string Path = "/api/v1/translations";'
    run_tree 1 "an API path in a dot-folder is scanned" "$tree"

    # obj/ and bin/ are skipped below the client folder only.
    tree="$(new_tree)"
    write_file "$tree" src/Patcher/Cli/Download.cs 'const string Path = "/api/v1/translation-files";'
    mkdir -p "$TMP_ROOT/bin.$cases/bin"
    mv "$tree" "$TMP_ROOT/bin.$cases/bin/repo"
    run_tree 1 "a repo inside a folder named bin is still scanned" "$TMP_ROOT/bin.$cases/bin/repo"

    # --- a hardcoded API path fails ---
    tree="$(new_tree)"
    write_file "$tree" src/Frontend/Pages/Editor.razor '<a href="/api/v1/translations/@Id">Edit</a>'
    run_tree 1 "an API path in a Frontend razor attribute fails" "$tree"
    expect_in_output "under src/Frontend"

    tree="$(new_tree)"
    write_file "$tree" src/Frontend/Pages/Client.cs 'private const string Base = "https://tms/api/v1/";'
    run_tree 1 "an API path in a Frontend string literal fails" "$tree"

    tree="$(new_tree)"
    write_file "$tree" src/Patcher/Cli/Download.cs 'var url = $"{baseUrl}/api/v2/translation-files/{lang}";'
    run_tree 1 "an API path in a Patcher string fails, whatever the version" "$tree"
    expect_in_output "under src/Patcher"
}

sh_runner="$TMP_ROOT/run-sh.sh"
printf '#!/usr/bin/env bash\nexec bash "$1/scripts/check-client-hypermedia.sh"\n' > "$sh_runner"
chmod +x "$sh_runner"
RUNNER="$sh_runner"
LABEL="sh"
run_suite

# With CDPATH exported, `cd` to a relative folder can print it, and the guard started by a relative
# path must not take that output for the repository root (#1048). The failing case proves that the
# guard still reads the files.
cdpath_runner="$TMP_ROOT/run-sh-cdpath.sh"
printf '#!/usr/bin/env bash\ncd "$1" && CDPATH=".:%s" exec bash scripts/check-client-hypermedia.sh\n' "$TMP_ROOT" > "$cdpath_runner"
chmod +x "$cdpath_runner"
RUNNER="$cdpath_runner"
LABEL="sh, CDPATH"
tree="$(new_tree)"
run_tree 0 "clean clients pass when the guard is started by a relative path" "$tree"
tree="$(new_tree)"
write_file "$tree" src/Patcher/Cli/Download.cs 'const string Path = "/api/v1/translation-files";'
run_tree 1 "an API path still fails when the guard is started by a relative path" "$tree"

if command -v pwsh >/dev/null 2>&1; then
    ps1_runner="$TMP_ROOT/run-ps1.sh"
    printf '#!/usr/bin/env bash\nexec pwsh -NoProfile -File "$1/scripts/check-client-hypermedia.ps1"\n' > "$ps1_runner"
    chmod +x "$ps1_runner"
    RUNNER="$ps1_runner"
    LABEL="ps1"
    run_suite
else
    printf 'i pwsh not found — skipped the check-client-hypermedia.ps1 twin suite.\n'
fi

printf 'All %d hypermedia guard case(s) passed.\n' "$cases"
