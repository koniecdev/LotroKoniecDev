#!/usr/bin/env bash
# Frontend markup guard: Static SSR purity, plus no inline <script> (CSP).
#
# The second rule is newer (#670): our CSP sends `script-src 'self'`, so an inline <script>
# in a component is dead on arrival and only the browser console reports it. It lives here
# rather than in a fifth guard script because this is already the frontend-markup gate CI
# runs in both workflows; the trade-off is that the file name now says less than it checks.
#
# The auth pages (src/AuthSystem, *.cshtml) got a CSP of their own in #693, so they are checked
# for what it blocks: an inline <script>, an inline <style> (#833), an inline event handler and a
# style= attribute. The Static-SSR rules do not apply there, because those pages are Razor Pages,
# not Blazor.
#
# The Frontend is Static SSR on purpose: no WebAssembly download, no SignalR circuit,
# no per-user server state. A single stray @rendermode, @onclick or StateHasChanged
# silently flips a page into interactive mode and quietly breaks that guarantee.
# CLAUDE.md states this rule in prose; THIS script is the machine that enforces it,
# so the rule can't rot. Run it before pushing; CI (pr-verify + ci) runs it on every PR.
#
# ONE deliberate exception: @onsubmit is allowed. It is the documented Static-SSR special
# case — "always functional, regardless of render mode" (MS docs:
# aspnet/core/blazor/components/class-libraries-and-static-server-side-rendering)
# — and is how Editor.razor posts forms (<form method="post" @formname @onsubmit>).
# Every other @on* handler (onclick/onchange/oninput/…) is dead in SSR and stays forbidden.
# A run that scans no file is not a pass (#1079): a folder that moved, or pages that changed their
# file type, would switch a rule off while CI stays green. So a missing folder, a folder with no file
# to scan, and a file grep cannot read all stop the guard with exit 2.
# Keep this in sync with its check-ssr-purity.ps1 twin (run locally on Windows).

set -euo pipefail

# In a UTF-8 locale GNU grep hides a matching line that is not valid UTF-8, so a line holding, say, a
# Windows-1250 letter was never checked (#1162). The C locale reads every byte as a plain character.
# There [[:space:]] is ASCII only, but Razor also reads a Unicode space such as U+2003 or U+00A0 as a
# space. So a rule for a space Razor skips uses [^!-~]: any byte that is not a visible ASCII character.
export LC_ALL=C

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
REPO_ROOT="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
FRONTEND_DIR="$REPO_ROOT/src/Frontend"
AUTH_DIR="$REPO_ROOT/src/AuthSystem"

for dir in "$FRONTEND_DIR" "$AUTH_DIR"; do
    if [ ! -d "$dir" ]; then
        echo "Frontend markup guard: directory not found at $dir" >&2
        exit 2
    fi
done

require_files() {
    # $1 = folder, $2 = the kind of file, for the message, then find's name tests.
    local dir="$1" kind="$2" found
    shift 2
    if ! found="$(find -H "$dir" \( -name obj -o -name bin \) -prune -o -type f \( "$@" \) -print)"; then
        echo "Frontend markup guard: could not list the files under $dir." >&2
        exit 2
    fi
    if [ -z "$found" ]; then
        echo "Frontend markup guard: no $kind under $dir, so nothing was checked." >&2
        exit 2
    fi
}

require_files "$FRONTEND_DIR" '.razor or .cs file' -name '*.razor' -o -name '*.cs'
require_files "$AUTH_DIR" '.cshtml page' -name '*.cshtml'

fail=0

stop_on_read_error() {
    # $1 = grep's exit code, $2 = the folder it scanned. 1 only means "no match", the clean result.
    if [ "$1" -gt 1 ]; then
        echo "Frontend markup guard: could not read the files under $2." >&2
        exit 2
    fi
}

check() {
    # $1 = grep -E pattern to flag, $2 = plain explanation of what to do instead,
    # $3 = optional grep -E allow-pattern: a token that IS valid in SSR (e.g. @onsubmit).
    #      It is stripped from each candidate line and $1 re-tested — so a line matching
    #      ONLY via the allowed token drops out, while a line that also carries a real
    #      violation (e.g. @onclick beside @onsubmit on one element) is still flagged.
    # obj/ and bin/ are build output, not source — never scan them (in CI this runs before
    # restore/build, but a local run after a build would otherwise hit generated *.g.cs).
    local matches rc=0
    matches="$(grep -rnE "$1" --include='*.razor' --include='*.cs' --exclude-dir=obj --exclude-dir=bin "$FRONTEND_DIR")" || rc=$?
    stop_on_read_error "$rc" "$FRONTEND_DIR"
    report "$matches" "$@"
}

check_auth_pages() {
    # Same arguments as check(), over the auth pages.
    local matches rc=0
    matches="$(grep -rnE "$1" --include='*.cshtml' --exclude-dir=obj --exclude-dir=bin "$AUTH_DIR")" || rc=$?
    stop_on_read_error "$rc" "$AUTH_DIR"
    report "$matches" "$@"
}

report() {
    # $1 = the candidate lines, then check()'s three arguments.
    local matches="$1"
    shift
    if [ -n "$matches" ] && [ -n "${3:-}" ]; then
        matches="$(printf '%s\n' "$matches" | sed -E "s/$3//g" | grep -E "$1" || true)"
    fi
    if [ -n "$matches" ]; then
        fail=1
        printf '✗ %s\n' "$2"
        echo "$matches" | sed 's/^/    /'
        echo
    fi
}

check '@rendermode' \
    'Render-mode directive forces a component into interactive mode. Remove it — SSR renders on the server.'
check '@on[a-z]+[^!-~]*[=:]' \
    'Blazor event handler (@onclick / @onchange / @oninput …) does nothing in SSR. Use <form method="post" @onsubmit> or <EditForm OnValidSubmit>.' \
    '@onsubmit[^!-~]*[=:]'
check 'StateHasChanged' \
    'StateHasChanged only works in interactive mode. In SSR the next request re-renders the page — delete the call.'
check 'AddInteractiveServerComponents|AddInteractiveWebAssemblyComponents|AddInteractiveServerRenderMode|AddInteractiveWebAssemblyRenderMode' \
    'Interactive Blazor registered in Program.cs. Keep only AddRazorComponents() and MapRazorComponents<App>().'
# HTML tag and attribute names are case-insensitive, so spell both out. The other rules stay
# case-sensitive on purpose: they match C#/Razor identifiers, where case is part of the name.
# Known gaps, accepted: the scan is line-based, so an opening tag split over two lines slips
# through (the smoke test folds newlines and still catches it), and a literal "<script>" inside
# a C# string would be flagged with no way to opt out. The auth-page rules below share the first
# gap: an onclick= or style= on the second line of a tag is not seen here. The auth integration
# tests read each rendered page as a whole and catch it on every state a plain GET shows.
check '<[sS][cC][rR][iI][pP][tT][^>]*>' \
    "Inline <script> in the Frontend. Our CSP sends script-src 'self', so the browser blocks it and only the console says so (#670). Move the code to a file under wwwroot and load it with src=, or drop it." \
    '<[sS][cC][rR][iI][pP][tT][^>]*[sS][rR][cC]=[^>]*>'
# The trailing class keeps List<ImportMapDefinition> and friends out of the match.
check '<ImportMap([^!-~]|[/>])' \
    "Blazor's import-map component renders an inline <script type=\"importmap\">, which script-src 'self' blocks on every page (#670). A Static SSR app never resolves a module specifier, so it has no job here."

check_auth_pages '<[sS][cC][rR][iI][pP][tT][^>]*>' \
    "Inline <script> in an auth page. The auth CSP sends script-src 'self', so the browser blocks it and only the console says so (#693). Move the code to a file under wwwroot, like login.js." \
    '<[sS][cC][rR][iI][pP][tT][^>]*[sS][rR][cC]=[^>]*>'
# A tag helper can rewrite a Unicode space inside a tag into an ASCII one: the form tag helper turns
# onsubmit<U+2003>= into a real onsubmit=. So every auth rule reads [^!-~] as a space too (#1162).
check_auth_pages '<[sS][tT][yY][lL][eE]([^!-~]|>)' \
    "Inline <style> in an auth page. The auth CSP sends style-src 'self', so the browser drops it and only the console says so (#833). Put the rules in wwwroot/account.css, under the page's body class if only one page needs them."
check_auth_pages '<[a-zA-Z][^>]*[^!-~][oO][nN][a-zA-Z]+[^!-~]*=' \
    "Inline event handler (onclick=, onsubmit=, …) in an auth page. The auth CSP sends script-src 'self', which blocks it (#693). Attach the handler from a file under wwwroot, like login.js."
check_auth_pages '<[a-zA-Z][^>]*[^!-~][sS][tT][yY][lL][eE][^!-~]*=' \
    "Inline style= attribute in an auth page. The auth CSP sends style-src 'self', which blocks it (#833). Use a class in wwwroot/account.css."

if [ "$fail" -ne 0 ]; then
    echo "──────────────────────────────────────────────────────────────────────"
    echo "Frontend markup guard FAILED."
    echo "See CLAUDE.md → 'Frontend is Static SSR'. Genuinely need interactivity, or an"
    echo "inline script? Both are architecture changes — write an ADR first (docs/adr/, /adr)."
    exit 1
fi

echo "✓ Frontend markup guard passed — Static SSR, no inline script, auth pages CSP-clean."
