#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Frontend markup guard (PowerShell twin of check-ssr-purity.sh): Static SSR purity, plus
    no inline <script> (CSP).

.DESCRIPTION
    The second rule is newer (#670): our CSP sends script-src 'self', so an inline <script> in
    a component is dead on arrival and only the browser console reports it. It lives here rather
    than in a fifth guard script because this is already the frontend-markup gate CI runs in both
    workflows; the trade-off is that the file name now says less than it checks.

    The auth pages (src/AuthSystem, *.cshtml) got a CSP of their own in #693, so they are checked
    for what it blocks: an inline <script>, a <style> without the per-request nonce, an inline
    event handler and a style= attribute. The Static-SSR rules do not apply there, because those
    pages are Razor Pages, not Blazor.

    The Frontend is Static SSR on purpose: no WebAssembly download, no SignalR circuit,
    no per-user server state. A single stray @rendermode, @onclick or StateHasChanged
    silently flips a page into interactive mode and quietly breaks that guarantee.
    CLAUDE.md states the rule in prose; this script enforces it. CI runs the .sh; this
    .ps1 is the local twin for Windows devs — keep the two in sync.

    Lifted from TheKittySaver with ONE deliberate deviation: @onsubmit is allowed. It is
    the documented Static-SSR special exception — "always functional, regardless of render
    mode" — and is how Editor.razor posts forms (<form method="post" @formname @onsubmit>).
    Every other @on* handler (onclick/onchange/oninput/...) is dead in SSR and stays forbidden.
#>

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $PSScriptRoot
$frontendDir = Join-Path $repoRoot 'src/Frontend'
$authDir     = Join-Path $repoRoot 'src/AuthSystem'

# A run that scans no file is not a pass (#1079), so a missing folder, a folder with no file to scan,
# and a file that cannot be read all stop the guard with exit 2, as in the .sh twin.
# Write-Host, not Write-Error: under ErrorActionPreference Stop, Write-Error throws, and the script
# would end with exit 1 before it reached `exit 2`.
foreach ($dir in $frontendDir, $authDir) {
    if (-not (Test-Path $dir)) {
        Write-Host "Frontend markup guard: directory not found at $dir"
        exit 2
    }
}

function Find-SourceFile {
    # Lists exactly the files the .sh twin's grep -r reads. It enters dot-folders (-Force; without it,
    # PowerShell on Linux and macOS skips them), never enters obj/ or bin/ (matched with case, like
    # --exclude-dir), skips symbolic links like grep -r does, and matches the name with case.
    param(
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][string] $NamePattern
    )

    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force -ErrorAction Stop) {
        if ($item.LinkType -eq 'SymbolicLink' -or $item.LinkType -eq 'Junction') { continue }
        if ($item.PSIsContainer) {
            if ($item.Name -ceq 'obj' -or $item.Name -ceq 'bin') { continue }
            Find-SourceFile -Directory $item.FullName -NamePattern $NamePattern
        }
        elseif ($item.Name -cmatch $NamePattern) {
            $item
        }
    }
}

# obj/ and bin/ are build output, not source — never scan them (mirrors the .sh --exclude-dir).
try {
    $files = @(Find-SourceFile -Directory $frontendDir -NamePattern '\.(razor|cs)$')
    $authPages = @(Find-SourceFile -Directory $authDir -NamePattern '\.cshtml$')
}
catch {
    Write-Host "Frontend markup guard: could not list the files to check: $($_.Exception.Message)"
    exit 2
}
if ($files.Count -eq 0) {
    Write-Host "Frontend markup guard: no .razor or .cs file under $frontendDir, so nothing was checked."
    exit 2
}
if ($authPages.Count -eq 0) {
    Write-Host "Frontend markup guard: no .cshtml page under $authDir, so nothing was checked."
    exit 2
}

$script:fail = $false

function Test-SsrPurity {
    # -Pattern flags a violation; -AllowPattern (optional) is an SSR-valid token (e.g. @onsubmit)
    # stripped before re-testing, so it can't mask a real handler sharing the line.
    param(
        [Parameter(Mandatory)][string] $Pattern,
        [Parameter(Mandatory)][string] $Message,
        [string] $AllowPattern,
        $Files = $files
    )

    try {
        $hits = $Files | Select-String -Pattern $Pattern -CaseSensitive -ErrorAction Stop
    }
    catch {
        Write-Host "Frontend markup guard: could not read the files to check: $($_.Exception.Message)"
        exit 2
    }
    if ($AllowPattern) {
        # Strip the SSR-valid token, then re-test: a line matching ONLY via the allowed
        # token drops out; a line also carrying a real handler is still flagged.
        $hits = $hits | Where-Object { ($_.Line -creplace $AllowPattern, '') -cmatch $Pattern }
    }

    if ($hits) {
        $script:fail = $true
        Write-Host "X $Message"
        foreach ($hit in $hits) {
            Write-Host ("    {0}:{1}:{2}" -f $hit.Path, $hit.LineNumber, $hit.Line.Trim())
        }
        Write-Host ""
    }
}

Test-SsrPurity -Pattern '@rendermode' `
    -Message 'Render-mode directive forces a component into interactive mode. Remove it - SSR renders on the server.'
Test-SsrPurity -Pattern '@on[a-z]+\s*[=:]' `
    -Message 'Blazor event handler (@onclick / @onchange / @oninput ...) does nothing in SSR. Use <form method="post" @onsubmit> or <EditForm OnValidSubmit>.' `
    -AllowPattern '@onsubmit\s*[=:]'
Test-SsrPurity -Pattern 'StateHasChanged' `
    -Message 'StateHasChanged only works in interactive mode. In SSR the next request re-renders the page - delete the call.'
Test-SsrPurity -Pattern 'AddInteractiveServerComponents|AddInteractiveWebAssemblyComponents|AddInteractiveServerRenderMode|AddInteractiveWebAssemblyRenderMode' `
    -Message 'Interactive Blazor registered in Program.cs. Keep only AddRazorComponents() and MapRazorComponents<App>().'
# HTML tag and attribute names are case-insensitive, so spell both out. The other rules stay
# case-sensitive on purpose: they match C#/Razor identifiers, where case is part of the name.
# The scan is line-based, like the .sh twin: an attribute on the second line of a split tag is not
# seen. The auth integration tests read each rendered page as a whole and catch it there.
Test-SsrPurity -Pattern '<[sS][cC][rR][iI][pP][tT][^>]*>' `
    -Message "Inline <script> in the Frontend. Our CSP sends script-src 'self', so the browser blocks it and only the console says so (#670). Move the code to a file under wwwroot and load it with src=, or drop it." `
    -AllowPattern '<[sS][cC][rR][iI][pP][tT][^>]*[sS][rR][cC]=[^>]*>'
# The trailing class keeps List<ImportMapDefinition> and friends out of the match.
Test-SsrPurity -Pattern '<ImportMap[\s/>]' `
    -Message "Blazor's import-map component renders an inline <script type=""importmap"">, which script-src 'self' blocks on every page (#670). A Static SSR app never resolves a module specifier, so it has no job here."

Test-SsrPurity -Files $authPages -Pattern '<[sS][cC][rR][iI][pP][tT][^>]*>' `
    -Message "Inline <script> in an auth page. The auth CSP sends script-src 'self', so the browser blocks it and only the console says so (#693). Move the code to a file under wwwroot, like login.js." `
    -AllowPattern '<[sS][cC][rR][iI][pP][tT][^>]*[sS][rR][cC]=[^>]*>'
Test-SsrPurity -Files $authPages -Pattern '<[sS][tT][yY][lL][eE](\s|>)' `
    -Message "<style> without the CSP nonce in an auth page. The auth CSP admits an inline style only by the response's nonce, so the browser drops it (#693). Write <style nonce=""@CspNonce.Get(HttpContext)"">." `
    -AllowPattern '<[sS][tT][yY][lL][eE][^>]*[nN][oO][nN][cC][eE]=[^>]*>'
Test-SsrPurity -Files $authPages -Pattern '<[a-zA-Z][^>]*\s[oO][nN][a-zA-Z]+\s*=' `
    -Message "Inline event handler (onclick=, onsubmit=, ...) in an auth page. The auth CSP sends script-src 'self', which blocks it (#693). Attach the handler from a file under wwwroot, like login.js."
Test-SsrPurity -Files $authPages -Pattern '<[a-zA-Z][^>]*\s[sS][tT][yY][lL][eE]\s*=' `
    -Message "Inline style= attribute in an auth page. The auth CSP admits inline styles only by nonce, and a nonce does not cover attributes (#693). Use a class in the page's <style> block."

if ($script:fail) {
    Write-Host "----------------------------------------------------------------------"
    Write-Host "Frontend markup guard FAILED."
    Write-Host "See CLAUDE.md -> 'Frontend is Static SSR'. Genuinely need interactivity, or an"
    Write-Host "inline script? Both are architecture changes - write an ADR first (docs/adr/, /adr)."
    exit 1
}

Write-Host "OK Frontend markup guard passed - Static SSR, no inline script, auth pages CSP-clean."
