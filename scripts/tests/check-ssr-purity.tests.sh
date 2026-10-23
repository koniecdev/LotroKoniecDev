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

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
TMP_ROOT="$(mktemp -d)"
# The unreadable-folder cases take the read bit away; give it back, or rm cannot clean up.
trap 'chmod -R u+rwx "$TMP_ROOT" 2>/dev/null; rm -rf "$TMP_ROOT"' EXIT

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
# src/Frontend/Components, $2 = its contents. The tree also holds one clean auth page: a run
# with no auth page to check fails (#1079).
new_tree() {
    local tree
    # Callers run this inside $(…), where set -e is off: an empty $tree would aim every step below
    # at the root or the current folder (#1042).
    tree="$(mktemp -d "$TMP_ROOT/tree.XXXXXX")" || return 1
    [ -d "$tree" ] || return 1
    mkdir -p "$tree/scripts" "$tree/src/Frontend/Components" "$tree/src/AuthSystem/Api/Pages/Account"
    cp "$GUARD_SH" "$GUARD_PS1" "$tree/scripts/"
    printf '%s\n' "$2" > "$tree/src/Frontend/Components/$1"
    printf '%s\n' '<p>ok</p>' > "$tree/src/AuthSystem/Api/Pages/Account/Placeholder.cshtml"
    printf '%s' "$tree"
}

run_case() {
    # $1 expected exit code, $2 description, $3 fixture file name, $4 fixture contents
    local expected="$1" desc="$2" tree rc=0
    tree="$(new_tree "$3" "$4")"
    run_tree "$expected" "$desc" "$tree"
}

# Same as run_case, but the fixture is an auth page (src/AuthSystem/…/Pages/Account/<$3>) next to
# a clean frontend component (#693).
run_auth_case() {
    local expected="$1" desc="$2" tree
    tree="$(new_tree Clean.razor '<p>ok</p>')"
    mkdir -p "$tree/src/AuthSystem/Api/Pages/Account"
    printf '%s\n' "$4" > "$tree/src/AuthSystem/Api/Pages/Account/$3"
    run_tree "$expected" "$desc" "$tree"
}

run_tree() {
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
    tree="$(new_tree App.razor '<p>ok</p>')"
    rm -rf "$tree/src/AuthSystem"
    run_tree 2 "a tree without the auth folder fails" "$tree"
    expect_in_output "directory not found"

    tree="$(new_tree App.razor '<p>ok</p>')"
    rm "$tree/src/AuthSystem/Api/Pages/Account/Placeholder.cshtml"
    printf '%s\n' 'namespace Auth;' > "$tree/src/AuthSystem/Api/Program.cs"
    run_tree 2 "an auth folder with no .cshtml page fails" "$tree"
    expect_in_output "nothing was checked"

    tree="$(new_tree App.razor '<p>ok</p>')"
    rm -rf "$tree/src/Frontend"
    run_tree 2 "a tree without the Frontend folder fails" "$tree"
    expect_in_output "directory not found"

    tree="$(new_tree app.css 'p { color: red; }')"
    run_tree 2 "a Frontend folder with no .razor or .cs file fails" "$tree"
    expect_in_output "nothing was checked"

    tree="$(new_tree app.css 'p { color: red; }')"
    mkdir -p "$tree/src/Frontend/obj/Debug"
    printf '%s\n' '<p>ok</p>' > "$tree/src/Frontend/obj/Debug/App.razor"
    run_tree 2 "a component under obj/ is build output, not a file to scan" "$tree"
    expect_in_output "nothing was checked"

    # PowerShell on Linux and macOS hides dot-folders unless -Force is given; grep does not.
    tree="$(new_tree App.razor '<p>ok</p>')"
    mkdir -p "$tree/src/Frontend/.shared"
    printf '%s\n' '<button @onclick="Go">Go</button>' > "$tree/src/Frontend/.shared/Bad.razor"
    run_tree 1 "a component in a dot-folder is scanned" "$tree"

    # obj/ and bin/ are skipped below the scanned folder only.
    tree="$(new_tree App.razor '<button @onclick="Go">Go</button>')"
    mkdir -p "$TMP_ROOT/bin.$cases/bin"
    mv "$tree" "$TMP_ROOT/bin.$cases/bin/repo"
    run_tree 1 "a repo inside a folder named bin is still scanned" "$TMP_ROOT/bin.$cases/bin/repo"

    # obj/ and bin/ are matched with case, as grep --exclude-dir does: Bin/ is a source folder.
    tree="$(new_tree App.razor '<p>ok</p>')"
    mkdir -p "$tree/src/Frontend/Bin"
    printf '%s\n' '<button @onclick="Go">Go</button>' > "$tree/src/Frontend/Bin/Bad.razor"
    run_tree 1 "a component in a folder named Bin is scanned" "$tree"

    # grep -r does not follow a symbolic link it meets inside the tree.
    tree="$(new_tree App.razor '<p>ok</p>')"
    printf '%s\n' '<button @onclick="Go">Go</button>' > "$TMP_ROOT/outside.$cases.razor"
    ln -s "$TMP_ROOT/outside.$cases.razor" "$tree/src/Frontend/Components/Linked.razor"
    run_tree 0 "a symbolic link to a component is not followed" "$tree"

    # Root ignores the read bit, so these cases prove nothing when the suite runs as root.
    if [ "$(id -u)" -ne 0 ]; then
        tree="$(new_tree App.razor '<p>ok</p>')"
        mkdir -p "$tree/src/Frontend/obj/Locked"
        chmod 000 "$tree/src/Frontend/obj/Locked"
        run_tree 0 "an unreadable folder under obj/ is never entered" "$tree"
        chmod 755 "$tree/src/Frontend/obj/Locked"

        tree="$(new_tree App.razor '<p>ok</p>')"
        mkdir -p "$tree/src/Frontend/Locked"
        chmod 000 "$tree/src/Frontend/Locked"
        run_tree 2 "a Frontend folder the guard cannot read fails the run" "$tree"
        expect_in_output "could not list"
        chmod 755 "$tree/src/Frontend/Locked"

        tree="$(new_tree App.razor '<button @onclick="Go">Go</button>')"
        chmod 000 "$tree/src/Frontend/Components/App.razor"
        run_tree 2 "a component the guard cannot read fails the run" "$tree"
        expect_in_output "could not read"
        chmod 644 "$tree/src/Frontend/Components/App.razor"

        tree="$(new_tree App.razor '<p>ok</p>')"
        chmod 000 "$tree/src/AuthSystem/Api/Pages/Account/Placeholder.cshtml"
        run_tree 2 "an auth page the guard cannot read fails the run" "$tree"
        expect_in_output "could not read"
        chmod 644 "$tree/src/AuthSystem/Api/Pages/Account/Placeholder.cshtml"
    else
        printf 'i [%s] running as root, which reads any file — skipped the unreadable-file cases.\n' "$LABEL"
    fi

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

    # In a UTF-8 locale GNU grep hides a matching line that is not valid UTF-8 (#1162). Here ę is
    # saved as one Windows-1250 byte.
    run_case 1 "an @onclick handler on a line that is not valid UTF-8 still fails" \
        App.razor $'<button @onclick="Save">Zapisz zmian\352</button>'

    # The guard runs in the C locale, where [[:space:]] is ASCII only. Razor also reads U+2003 and
    # U+00A0 as spaces (#1162).
    run_case 1 "an @onclick handler with U+2003 before = still fails" \
        App.razor $'<button @onclick\342\200\203="Save">Go</button>'
    run_case 1 "an @onclick handler with U+00A0 before = fails" \
        App.razor $'<button @onclick\302\240="Save">Go</button>'
    run_case 0 "an @onsubmit handler with U+2003 before = is still allowed" \
        App.razor $'<form method="post" @onsubmit\342\200\203="Save"></form>'
    run_case 1 "the import-map component with U+2003 after its name fails" App.razor $'<ImportMap\342\200\203/>'

    # --- the auth pages: the same CSP rules, over *.cshtml (#693) ---
    run_auth_case 0 "an auth page with a stylesheet link and an external script passes" \
        Login.cshtml '<link rel="stylesheet" href="/account.css" /><script src="/login.js"></script>'
    run_auth_case 1 "an inline script in an auth page fails" \
        Login.cshtml '<script>(function () {})();</script>'
    expect_in_output "#693"
    run_auth_case 1 "an inline style in an auth page fails" \
        Login.cshtml '<style>p{}</style>'
    expect_in_output "Inline <style> in an auth page"
    run_auth_case 1 "an inline style on a line that is not valid UTF-8 still fails" \
        Login.cshtml $'<style>p{} /* zmian\352 */</style>'
    run_auth_case 1 "an inline style after U+2003 fails" \
        Login.cshtml $'<style\342\200\203media="screen">p{}</style>'
    run_auth_case 1 "an UPPERCASE inline style fails" \
        Login.cshtml '<STYLE media="screen">p{}</STYLE>'
    # style-src is 'self' since #833, so a nonce no longer lets a style block through.
    run_auth_case 1 "a style with a nonce fails too" \
        Login.cshtml '<style nonce="@n">a{}</style>'
    run_auth_case 0 "a stylesheet link is not an inline style" \
        Login.cshtml '<link rel="stylesheet" href="/fonts.css" />'
    run_auth_case 0 "the Static-SSR rules do not apply to Razor Pages" \
        Login.cshtml '<p>@rendermode and StateHasChanged are Blazor words.</p>'
    run_auth_case 1 "an inline event handler in an auth page fails" \
        Login.cshtml '<button type="button" onclick="toggle()">Pokaż</button>'
    expect_in_output "Inline event handler"
    run_auth_case 1 "an UPPERCASE inline event handler fails" \
        Login.cshtml '<form ONSUBMIT="go()"></form>'
    # The form tag helper turns a Unicode space here into an ASCII one, so the browser gets a real
    # handler (#1162).
    run_auth_case 1 "an inline event handler with U+2003 before = fails" \
        Login.cshtml $'<form method="post" onsubmit\342\200\203="go()"></form>'
    run_auth_case 1 "an inline event handler after U+00A0 fails" \
        Login.cshtml $'<form\302\240onsubmit="go()"></form>'
    run_auth_case 1 "a style attribute in an auth page fails" \
        Login.cshtml '<div style="width: 50%"></div>'
    expect_in_output "Inline style= attribute"
    run_auth_case 1 "a style attribute with U+2003 before = fails" \
        Login.cshtml $'<form method="post" style\342\200\203="width: 50%"></form>'
    run_auth_case 0 "an attribute value that starts with 'on' is not a handler" \
        Login.cshtml '<input autocomplete="one-time-code" class="icon only" />'
    run_auth_case 0 "a word like 'online' in text is not a handler" \
        Login.cshtml '<p>Jesteś online = zalogowany</p>'
    run_auth_case 0 "a style in a C# file of the auth system is out of scope" \
        Template.cs 'const string Html = "<style>p{}</style>";'
}

sh_runner="$TMP_ROOT/run-sh.sh"
printf '#!/usr/bin/env bash\nexec bash "$1/scripts/check-ssr-purity.sh"\n' > "$sh_runner"
chmod +x "$sh_runner"
RUNNER="$sh_runner"
LABEL="sh"
run_suite

# With CDPATH exported, `cd` to a relative folder can print it, and the guard started by a relative
# path must not take that output for the repository root (#1048). The failing case proves that the
# guard still reads the files.
cdpath_runner="$TMP_ROOT/run-sh-cdpath.sh"
printf '#!/usr/bin/env bash\ncd "$1" && CDPATH=".:%s" exec bash scripts/check-ssr-purity.sh\n' "$TMP_ROOT" > "$cdpath_runner"
chmod +x "$cdpath_runner"
RUNNER="$cdpath_runner"
LABEL="sh, CDPATH"
run_case 0 "a clean tree passes when the guard is started by a relative path" App.razor '<p>ok</p>'
run_case 1 "an inline script still fails when the guard is started by a relative path" \
    App.razor '<script>alert(1)</script>'

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
