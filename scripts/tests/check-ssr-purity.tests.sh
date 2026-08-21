#!/usr/bin/env bash
# Test suite for the Frontend markup guard (check-ssr-purity.sh / .ps1).
#
# The guard only ever runs against a clean tree in CI, so "it passed" proves it did not
# false-positive — never that it still FIRES. These cases prove both directions on a
# throwaway fixture tree, with one case per rule and per known trap:
# HTML tag/attribute case (#670), the src= allow-pattern, and the <ImportMap literal.
# CI runs this right before the guard itself, so the gate cannot rot silently.
# When pwsh is available (it is on the ubuntu runners), the whole suite re-runs against
# the .ps1 twin, keeping the two in sync mechanically.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

GUARD_SH="$SCRIPTS_DIR/check-ssr-purity.sh"
GUARD_PS1="$SCRIPTS_DIR/check-ssr-purity.ps1"
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

# Builds a fixture repo holding a copy of the guard, so the guard resolves src/Frontend
# relative to itself exactly as it does in the real tree. $1 = file name under
# src/Frontend/Components, $2 = its contents.
new_tree() {
    local tree
    tree="$(mktemp -d "$TMP_ROOT/tree.XXXXXX")"
    mkdir -p "$tree/scripts" "$tree/src/Frontend/Components"
    cp "$GUARD_SH" "$GUARD_PS1" "$tree/scripts/"
    printf '%s\n' "$2" > "$tree/src/Frontend/Components/$1"
    printf '%s' "$tree"
}

run_case() {
    # $1 expected exit code, $2 description, $3 fixture file name, $4 fixture contents
    local expected="$1" desc="$2" tree rc=0
    tree="$(new_tree "$3" "$4")"
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
    # --- the inline-script rule (#670) ---
    run_case 0 "external lowercase script passes" \
        App.razor '<script src="/app.js"></script>'
    run_case 1 "inline lowercase script fails" \
        App.razor '<script>alert(1)</script>'
    expect_in_output "#670"
    run_case 1 "the import-map script shape fails" \
        App.razor '<script type="importmap">{}</script>'

    # HTML tag and attribute names are case-insensitive. Both directions used to be wrong.
    run_case 1 "inline UPPERCASE script fails" \
        App.razor '<SCRIPT>alert(1)</SCRIPT>'
    run_case 0 "external UPPERCASE SRC passes" \
        App.razor '<script SRC="/app.js"></script>'
    run_case 0 "external MixedCase tag and attribute passes" \
        App.razor '<Script Src="/app.js"></Script>'

    run_case 1 "an inline script beside an external one on the same line fails" \
        App.razor '<script src="/a.js"></script><script>alert(1)</script>'
    run_case 0 "src= inside razor markup text is not a script tag" \
        App.razor '<p>Use src= to load a file.</p>'

    # --- the import-map component rule (#670) ---
    run_case 1 "the import-map component fails" App.razor '<ImportMap/>'
    run_case 1 "the import-map component with a space fails" App.razor '<ImportMap />'
    run_case 0 "a generic type argument named ImportMapDefinition passes" \
        Thing.cs 'private readonly List<ImportMapDefinition> _maps = [];'

    # --- the Static-SSR rules must keep working ---
    run_case 1 "a render-mode directive still fails" App.razor '@rendermode InteractiveServer'
    run_case 1 "an @onclick handler still fails" App.razor '<button @onclick="Go">Go</button>'
    run_case 0 "an @onsubmit handler is still allowed" \
        App.razor '<form method="post" @onsubmit="Save"></form>'
    run_case 1 "StateHasChanged still fails" Thing.cs 'StateHasChanged();'
}

sh_runner="$TMP_ROOT/run-sh.sh"
printf '#!/usr/bin/env bash\nexec bash "$1/scripts/check-ssr-purity.sh"\n' > "$sh_runner"
chmod +x "$sh_runner"
RUNNER="$sh_runner"
LABEL="sh"
run_suite

if command -v pwsh >/dev/null 2>&1; then
    ps1_runner="$TMP_ROOT/run-ps1.sh"
    printf '#!/usr/bin/env bash\nexec pwsh -NoProfile -File "$1/scripts/check-ssr-purity.ps1"\n' > "$ps1_runner"
    chmod +x "$ps1_runner"
    RUNNER="$ps1_runner"
    LABEL="ps1"
    run_suite
else
    printf 'i pwsh not found — skipped the check-ssr-purity.ps1 twin suite.\n'
fi

printf 'All %d Frontend markup guard case(s) passed.\n' "$cases"
