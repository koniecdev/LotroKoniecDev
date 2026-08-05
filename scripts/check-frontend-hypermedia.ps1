#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Hypermedia guard for the Frontend (PowerShell twin of check-frontend-hypermedia.sh).

.DESCRIPTION
    There is no API gateway (ADR-0041): each API's discovery document IS the client contract
    surface, so the Frontend resolves every entry point by REL NAME through IDiscoveryCache and
    follows the href the server hands back. It must never carry an API path of its own - a
    hardcoded "/api/v1/..." silently reintroduces the coupling #610 removed, and it fails at
    runtime (404, or worse a call the caller was never authorized to make) instead of at build time.

    CLAUDE.md and ADR-0041 state the rule in prose; this script enforces it. CI runs the .sh;
    this .ps1 is the local twin for Windows devs - keep the two in sync.

    What it flags: an API path inside a STRING LITERAL (the shape a hardcoded route actually
    takes - a const, an interpolation base, a razor href). Prose mentions in comments and XML
    docs are deliberately allowed: documenting which route a rel points at is useful, and
    banning that only pushes people to weaken the guard.
#>

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $PSScriptRoot
$frontendDir = Join-Path $repoRoot 'src/Frontend'

if (-not (Test-Path $frontendDir)) {
    Write-Error "Hypermedia guard: Frontend directory not found at $frontendDir"
    exit 2
}

# obj/ and bin/ are build output, not source - never scan them (mirrors the .sh --exclude-dir).
$files = Get-ChildItem -Path $frontendDir -Recurse -File -Include '*.razor', '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }
$script:fail = $false

function Test-Hypermedia {
    param(
        [Parameter(Mandatory)][string] $Pattern,
        [Parameter(Mandatory)][string] $Message
    )

    $hits = $files | Select-String -Pattern $Pattern -CaseSensitive
    if ($hits) {
        $script:fail = $true
        Write-Host "X $Message"
        foreach ($hit in $hits) {
            Write-Host ("    {0}:{1}:{2}" -f $hit.Path, $hit.LineNumber, $hit.Line.Trim())
        }
        Write-Host ""
    }
}

# A double-quoted literal containing a versioned API path. C# strings and razor attributes both
# use double quotes, so one pattern covers every real occurrence; the version is matched loosely
# so a future /api/v2 cannot slip through.
Test-Hypermedia -Pattern '"[^"]*api/v[0-9]' `
    -Message 'A TMS API path is hardcoded in a string literal. Resolve the entry point by rel instead: IDiscoveryCache.ResolveTranslationSystemHrefAsync(Rels.<Name>), or follow the href a loaded resource already advertises.'

if ($script:fail) {
    Write-Host "----------------------------------------------------------------------"
    Write-Host "Hypermedia guard FAILED - the Frontend must not hardcode API paths."
    Write-Host "See ADR-0041 and CLAUDE.md -> 'No API gateway'. A client takes one root URL"
    Write-Host "per service as config and resolves everything else by rel name."
    exit 1
}

Write-Host "OK Hypermedia guard passed - Frontend resolves its entry points by rel."
