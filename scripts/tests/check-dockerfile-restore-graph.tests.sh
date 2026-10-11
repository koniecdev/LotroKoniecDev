#!/usr/bin/env bash
# Test suite for the Docker restore-graph guard (check-dockerfile-restore-graph.sh / .ps1).
#
# The guard only ever runs against a clean tree in CI, so "it passed" proves it did not
# false-positive — never that it still FIRES, and never that it checked anything at all (#1079).
# These cases prove both directions on throwaway fixture trees: a complete COPY list passes, a
# missing one fails, and a tree where the guard finds nothing to check fails with exit 2.
# CI runs this right before the guard itself, so the gate cannot rot silently.
# When pwsh is available (it is on the ubuntu runners), the whole suite re-runs against
# the .ps1 twin, so both twins must give the same exit code on every case.

set -euo pipefail

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
TMP_ROOT="$(mktemp -d)"
# The unreadable-folder case takes the read bit away; give it back, or rm cannot clean up.
trap 'chmod -R u+rwx "$TMP_ROOT" 2>/dev/null; rm -rf "$TMP_ROOT"' EXIT

GUARD_SH="$SCRIPTS_DIR/check-dockerfile-restore-graph.sh"
GUARD_PS1="$SCRIPTS_DIR/check-dockerfile-restore-graph.ps1"
LABEL=""
RUNNER=""
LAST_OUTPUT=""
cases=0

# App -> Lib -> Core. The references use backslashes and "..", as the real .csproj files do.
APP_CSPROJ='<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup></Project>'
LIB_CSPROJ='<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\Core\Core.csproj" /></ItemGroup></Project>'
CORE_CSPROJ='<Project Sdk="Microsoft.NET.Sdk"></Project>'

COPY_APP='COPY ["src/App/App.csproj", "src/App/"]'
COPY_LIB='COPY ["src/Lib/Lib.csproj", "src/Lib/"]'
COPY_CORE='COPY ["src/Core/Core.csproj", "src/Core/"]'
RESTORE_APP='RUN dotnet restore "src/App/App.csproj"'

fail() {
    printf '✗ [%s] %s\n' "$LABEL" "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    exit 1
}

# Builds a fixture repo holding a copy of the guard, so the guard resolves the repo root relative
# to itself exactly as it does in the real tree. It holds the three projects and no Dockerfile.
new_tree() {
    local tree
    tree="$(mktemp -d "$TMP_ROOT/tree.XXXXXX")"
    mkdir -p "$tree/scripts" "$tree/src/App" "$tree/src/Lib" "$tree/src/Core"
    cp "$GUARD_SH" "$GUARD_PS1" "$tree/scripts/"
    printf '%s\n' "$APP_CSPROJ" > "$tree/src/App/App.csproj"
    printf '%s\n' "$LIB_CSPROJ" > "$tree/src/Lib/Lib.csproj"
    printf '%s\n' "$CORE_CSPROJ" > "$tree/src/Core/Core.csproj"
    printf '%s' "$tree"
}

# $1 = tree, $2 = Dockerfile path under the tree, then one argument per Dockerfile line.
write_dockerfile() {
    local tree="$1" path="$2"
    shift 2
    mkdir -p "$(dirname "$tree/$path")"
    printf '%s\n' 'FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build' 'WORKDIR /src' "$@" 'COPY . .' > "$tree/$path"
}

# Writes the one Dockerfile that copies the whole App -> Lib -> Core closure.
write_complete_dockerfile() {
    write_dockerfile "$1" src/App/Dockerfile "$COPY_APP" "$COPY_LIB" "$COPY_CORE" "$RESTORE_APP"
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

    # --- the guard checks something, or it does not pass (#1079) ---
    tree="$(new_tree)"
    run_tree 2 "a tree with no Dockerfile fails" "$tree"
    expect_in_output "nothing was checked"

    tree="$(new_tree)"
    write_dockerfile "$tree" Dockerfile.tests 'COPY ["src/", "src/"]' "$RESTORE_APP"
    run_tree 2 "a tree whose only Dockerfile copies whole folders fails" "$tree"
    expect_in_output "nothing was checked"

    tree="$(new_tree)"
    write_dockerfile "$tree" src/App/App.dockerfile "$COPY_APP" "$COPY_LIB" "$COPY_CORE" "$RESTORE_APP"
    run_tree 2 "a Dockerfile renamed out of the Dockerfile* pattern is not checked, so the tree fails" "$tree"
    expect_in_output "nothing was checked"

    # Each of these holds a COPY list with no restore, which fails the run if the guard reads it.
    tree="$(new_tree)"
    write_complete_dockerfile "$tree"
    write_dockerfile "$tree" node_modules/pkg/Dockerfile "$COPY_APP"
    write_dockerfile "$tree" src/App/node_modules/pkg/Dockerfile "$COPY_APP"
    write_dockerfile "$tree" .claude/worktrees/ticket-1/Dockerfile "$COPY_APP"
    write_dockerfile "$tree" .git/Dockerfile "$COPY_APP"
    run_tree 0 "Dockerfiles under node_modules, .claude and .git are not checked" "$tree"
    expect_in_output "1 Dockerfile(s)"

    tree="$(new_tree)"
    write_dockerfile "$tree" node_modules/pkg/Dockerfile "$COPY_APP" "$COPY_LIB" "$COPY_CORE" "$RESTORE_APP"
    run_tree 2 "a tree whose only Dockerfile is under node_modules fails" "$tree"
    expect_in_output "nothing was checked"

    # Only the repo's own .claude is skipped; a .claude folder deeper down is an ordinary folder.
    tree="$(new_tree)"
    write_complete_dockerfile "$tree"
    write_dockerfile "$tree" src/.claude/Dockerfile "$COPY_APP" "$COPY_LIB" "$RESTORE_APP"
    run_tree 1 "a Dockerfile under a .claude folder below the root is checked" "$tree"
    expect_in_output "missing: src/Core/Core.csproj"

    # find -name is case-sensitive, so a lowercase dockerfile is not one; both twins must agree.
    tree="$(new_tree)"
    write_complete_dockerfile "$tree"
    write_dockerfile "$tree" src/Lib/dockerfile "$COPY_APP"
    run_tree 0 "a lowercase dockerfile does not match the Dockerfile* pattern" "$tree"
    expect_in_output "1 Dockerfile(s)"

    # PowerShell on Linux and macOS hides dot-folders unless -Force is given; find does not.
    tree="$(new_tree)"
    write_complete_dockerfile "$tree"
    write_dockerfile "$tree" .docker/Dockerfile "$COPY_APP" "$COPY_LIB" "$RESTORE_APP"
    run_tree 1 "a Dockerfile under a dot-folder like .docker is checked" "$tree"
    expect_in_output "missing: src/Core/Core.csproj"

    tree="$(new_tree)"
    write_complete_dockerfile "$tree"
    ln -s "$tree" "$TMP_ROOT/link.$cases"
    run_tree 0 "a repo root reached through a symbolic link is still read" "$TMP_ROOT/link.$cases"
    expect_in_output "1 Dockerfile(s)"

    tree="$(new_tree)"
    write_complete_dockerfile "$tree"
    mkdir -p "$TMP_ROOT/outside.$cases"
    write_dockerfile "$TMP_ROOT/outside.$cases" Dockerfile "$COPY_APP"
    ln -s "$TMP_ROOT/outside.$cases" "$tree/src/linked"
    run_tree 0 "a symbolic link to a folder inside the tree is not followed, as with find" "$tree"
    expect_in_output "1 Dockerfile(s)"

    # Root ignores the read bit, so this case proves nothing when the suite runs as root.
    if [ "$(id -u)" -ne 0 ]; then
        tree="$(new_tree)"
        write_complete_dockerfile "$tree"
        mkdir -p "$tree/src/Locked"
        chmod 000 "$tree/src/Locked"
        run_tree 2 "a folder the guard cannot read fails the run" "$tree"
        expect_in_output "could not list the Dockerfiles"
        chmod 755 "$tree/src/Locked"
    else
        printf 'i [%s] running as root, which reads any folder — skipped the unreadable-folder case.\n' "$LABEL"
    fi

    # --- a complete COPY list passes ---
    tree="$(new_tree)"
    write_complete_dockerfile "$tree"
    run_tree 0 "a Dockerfile that copies the whole closure passes" "$tree"
    expect_in_output "1 Dockerfile(s)"

    tree="$(new_tree)"
    write_complete_dockerfile "$tree"
    write_dockerfile "$tree" Dockerfile.tests 'COPY ["src/", "src/"]' "$RESTORE_APP"
    run_tree 0 "a whole-folder Dockerfile next to a checked one is skipped, not failed" "$tree"
    expect_in_output "1 Dockerfile(s)"

    tree="$(new_tree)"
    write_dockerfile "$tree" src/App/Dockerfile "$COPY_APP" "$COPY_LIB" "$COPY_CORE" \
        'RUN dotnet restore \' '    "src/App/App.csproj"'
    run_tree 0 "a restore root on a continuation line is read" "$tree"

    # --- a missing COPY line fails ---
    tree="$(new_tree)"
    write_dockerfile "$tree" src/App/Dockerfile "$COPY_APP" "$COPY_LIB" "$RESTORE_APP"
    run_tree 1 "a Dockerfile without the COPY line of a transitive reference fails" "$tree"
    expect_in_output "missing: src/Core/Core.csproj"

    tree="$(new_tree)"
    write_dockerfile "$tree" src/App/Dockerfile "$COPY_APP" "$COPY_CORE" "$RESTORE_APP"
    run_tree 1 "a Dockerfile without the COPY line of a direct reference fails" "$tree"
    expect_in_output "missing: src/Lib/Lib.csproj"

    tree="$(new_tree)"
    write_dockerfile "$tree" src/App/Dockerfile "$COPY_APP" 'COPY ["src/lib/lib.csproj", "src/lib/"]' "$COPY_CORE" "$RESTORE_APP"
    run_tree 1 "a COPY path that differs only in case fails, as on the Linux image build" "$tree"
    expect_in_output "missing: src/Lib/Lib.csproj"

    tree="$(new_tree)"
    write_dockerfile "$tree" src/App/Dockerfile "$COPY_APP" "$COPY_LIB" "$COPY_CORE" "$RESTORE_APP"
    printf '%s\n' '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\Gone\Gone.csproj" /></ItemGroup></Project>' \
        > "$tree/src/Core/Core.csproj"
    run_tree 1 "a reference to a project that is not on disk fails" "$tree"
    expect_in_output "src/Gone/Gone.csproj"

    tree="$(new_tree)"
    write_dockerfile "$tree" src/App/Dockerfile "$COPY_APP" "$COPY_LIB" "$COPY_CORE" '# RUN dotnet restore "src/App/App.csproj"'
    run_tree 1 "a commented-out restore is not a restore root" "$tree"
    expect_in_output "never runs"
}

sh_runner="$TMP_ROOT/run-sh.sh"
printf '#!/usr/bin/env bash\nexec bash "$1/scripts/check-dockerfile-restore-graph.sh"\n' > "$sh_runner"
chmod +x "$sh_runner"
RUNNER="$sh_runner"
LABEL="sh"
run_suite

# With CDPATH exported, `cd` to a relative folder can print it, and the guard started by a relative
# path must not take that output for the repository root (#1048). Before that fix, this broke find
# and the guard passed with 0 Dockerfiles checked: the failing case proves that it still reads them.
cdpath_runner="$TMP_ROOT/run-sh-cdpath.sh"
printf '#!/usr/bin/env bash\ncd "$1" && CDPATH=".:%s" exec bash scripts/check-dockerfile-restore-graph.sh\n' "$TMP_ROOT" > "$cdpath_runner"
chmod +x "$cdpath_runner"
RUNNER="$cdpath_runner"
LABEL="sh, CDPATH"
tree="$(new_tree)"
write_complete_dockerfile "$tree"
run_tree 0 "a complete tree passes when the guard is started by a relative path" "$tree"
expect_in_output "1 Dockerfile(s)"
tree="$(new_tree)"
write_dockerfile "$tree" src/App/Dockerfile "$COPY_APP" "$COPY_LIB" "$RESTORE_APP"
run_tree 1 "a missing COPY line still fails when the guard is started by a relative path" "$tree"

if command -v pwsh >/dev/null 2>&1; then
    ps1_runner="$TMP_ROOT/run-ps1.sh"
    printf '#!/usr/bin/env bash\nexec pwsh -NoProfile -File "$1/scripts/check-dockerfile-restore-graph.ps1"\n' > "$ps1_runner"
    chmod +x "$ps1_runner"
    RUNNER="$ps1_runner"
    LABEL="ps1"
    run_suite
else
    printf 'i pwsh not found — skipped the check-dockerfile-restore-graph.ps1 twin suite.\n'
fi

printf 'All %d Docker restore-graph guard case(s) passed.\n' "$cases"
