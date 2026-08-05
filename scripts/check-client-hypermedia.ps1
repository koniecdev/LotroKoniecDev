#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Hypermedia guard for every API client (PowerShell twin of check-client-hypermedia.sh).

.DESCRIPTION
    There is no API gateway (ADR-0041): each API's discovery document IS the client contract
    surface, so a client - the Frontend (#610) and the patcher CLI (#611) - resolves every entry
    point by REL NAME and follows the href the server hands back. It must never carry an API path
    of its own - a hardcoded "/api/v1/..." silently reintroduces the coupling those tickets
    removed, and it fails at runtime (404, or worse a call the caller was never authorized to
    make) instead of at build time. The CLI is the sharper case: it ships to players' machines and
    cannot be updated remotely, so a path baked into it is a permanent commitment.

    CLAUDE.md and ADR-0041 state the rule in prose; this script enforces it. CI runs the .sh;
    this .ps1 is the local twin for Windows devs - keep the two in sync.

    What it flags: an API path inside a STRING LITERAL (the shape a hardcoded route actually
    takes - a const, an interpolation base, a razor href). Prose mentions in comments and XML
    docs are deliberately allowed: documenting which route a rel points at is useful, and
    banning that only pushes people to weaken the guard.
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# A double-quoted literal containing a versioned API path. C# strings and razor attributes both
# use double quotes, so one pattern covers every real occurrence; the version is matched loosely
# so a future /api/v2 cannot slip through.
$apiPathPattern = '"[^"]*api/v[0-9]'

$script:fail = $false

function Test-Hypermedia {
    param(
        [Parameter(Mandatory)][string] $Client,
        [Parameter(Mandatory)][string] $Message
    )

    $dir = Join-Path $repoRoot "src/$Client"
    if (-not (Test-Path $dir)) {
        Write-Error "Hypermedia guard: client directory not found at $dir"
        exit 2
    }

    # obj/ and bin/ are build output, not source - never scan them (mirrors the .sh --exclude-dir).
    $files = Get-ChildItem -Path $dir -Recurse -File -Include '*.razor', '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }

    $hits = $files | Select-String -Pattern $apiPathPattern -CaseSensitive
    if ($hits) {
        $script:fail = $true
        Write-Host "X $Message"
        foreach ($hit in $hits) {
            Write-Host ("    {0}:{1}:{2}" -f $hit.Path, $hit.LineNumber, $hit.Line.Trim())
        }
        Write-Host ""
    }
}

Test-Hypermedia -Client 'Frontend' `
    -Message 'A TMS API path is hardcoded in a string literal under src/Frontend. Resolve the entry point by rel instead: IDiscoveryCache.ResolveTranslationSystemHrefAsync(Rels.<Name>), or follow the href a loaded resource already advertises.'

Test-Hypermedia -Client 'Patcher' `
    -Message 'A TMS API path is hardcoded in a string literal under src/Patcher. The CLI ships to players and cannot be updated remotely, so a baked-in path is permanent - resolve the endpoint by rel through ITranslationFileEndpointResolver instead.'

if ($script:fail) {
    Write-Host "----------------------------------------------------------------------"
    Write-Host "Hypermedia guard FAILED - API clients must not hardcode API paths."
    Write-Host "See ADR-0041 and CLAUDE.md -> 'No API gateway'. A client takes one root URL"
    Write-Host "per service as config and resolves everything else by rel name."
    exit 1
}

Write-Host "OK Hypermedia guard passed - the Frontend and the CLI resolve their entry points by rel."
