#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Post-deploy smoke test (M6-13) — PowerShell twin of smoke.sh.

.DESCRIPTION
    One command that gives a green/red signal that a deployed environment came up correctly, without
    manual clicking. Run it after every deploy (staging or production), or against the local
    prod-parity / dev stack. It exercises the five legs that actually break on a deploy:

      1. Health      auth-api + tms-api /health/ready return 200; the frontend root responds (it has
                     no /health endpoint — it is a Static-SSR app, so "the page serves" is liveness).
      2. FE assets   the frontend image actually shipped its static web assets. "The page serves" is
                     NOT enough: a [StreamRendering] page returns 200 with its spinner frame before it
                     fetches anything, so an image whose static-web-assets manifest lost _framework/*
                     passes leg 1 while every asset 404s and the browser spins forever (#414). The
                     fingerprint in blazor.web.<hash>.js is the tell — @Assets[] emits it only when it
                     resolved the manifest, so an unfingerprinted src means the manifest is empty.
                     The same leg also checks that the CSP we send does not block the HTML we send: an
                     inline <script> under script-src 'self' is dead, the page still renders, and only
                     the browser console says so, so no other check here can see it (#670).
      3. Auth token  a client-credentials token round-trip against auth-api's /connect/token — the
                     only non-interactive OIDC grant available in staging/production (the web client
                     needs a browser; the password-flow client is seeded only in Testing).
      4. Token accept tms-api ACCEPTS that token: an anonymous call to a protected read is 401, and
                     the same call WITH the bearer token is NOT 401 (it is 403 — the service account
                     has no role; every TMS endpoint is role-gated). 401-with-a-valid-token is the
                     classic "works locally, breaks on staging" issuer/audience/JWKS mismatch
                     (runbook -> "Consistency rules that bite"), so 403-vs-401 is the high-value check.
      5. Distribution the public translation-file endpoint serves the artifact with an ETag and
                     honours If-None-Match with a 304 (the CLI/player relies on this; spec 0001).

    Clear pass/fail per check; NON-ZERO exit on any failure (exit 1). Usage/config problems exit 2.
    A not-yet-seeded environment (no artifact built) WARNS on leg 5 rather than failing. Keep in
    sync with smoke.sh. Requires PowerShell 7+ (for -SkipHttpErrorCheck).

.EXAMPLE
    # A real environment (publicly-trusted ingress cert — no -Insecure):
    $env:SMOKE_CLIENT_SECRET = '...'
    ./scripts/smoke.ps1 -AuthUrl https://auth.lotro-translator.pl -TmsUrl https://tms.lotro-translator.pl -FrontendUrl https://lotro-translator.pl

.EXAMPLE
    # The local dev stack (host Kestrels + untrusted dev cert):
    ./scripts/smoke.ps1 -AuthUrl https://localhost:5003 -TmsUrl https://localhost:5002 -FrontendUrl https://localhost:7017 `
        -ClientSecret dev-api-secret-min-32-characters-long -Insecure
#>

param(
    [string] $AuthUrl      = $env:SMOKE_AUTH_URL,
    [string] $TmsUrl       = $env:SMOKE_TMS_URL,
    [string] $FrontendUrl  = $env:SMOKE_FRONTEND_URL,
    [string] $ClientId     = $(if ($env:SMOKE_CLIENT_ID) { $env:SMOKE_CLIENT_ID } else { 'lotrokoniecdev-api' }),
    [string] $ClientSecret = $env:SMOKE_CLIENT_SECRET,
    [string] $Scope        = $(if ($env:SMOKE_SCOPE) { $env:SMOKE_SCOPE } else { 'service' }),
    [string] $Lang         = $(if ($env:SMOKE_LANG) { $env:SMOKE_LANG } else { 'pl' }),
    [int]    $TimeoutSec   = $(if ($env:SMOKE_TIMEOUT) { [int]$env:SMOKE_TIMEOUT } else { 15 }),
    [switch] $Insecure,
    # A frontend response with no Content-Security-Policy FAILS instead of warning. CD passes it:
    # only a Development stack legitimately serves no CSP, and CD never smokes one.
    [switch] $RequireCsp
)

$ErrorActionPreference = 'Stop'

if (-not $Insecure -and $env:SMOKE_INSECURE -eq '1') { $Insecure = $true }
if (-not $RequireCsp -and $env:SMOKE_REQUIRE_CSP -eq '1') { $RequireCsp = $true }

$missing = @()
if (-not $AuthUrl)      { $missing += '-AuthUrl' }
if (-not $TmsUrl)       { $missing += '-TmsUrl' }
if (-not $FrontendUrl)  { $missing += '-FrontendUrl' }
if (-not $ClientSecret) { $missing += '-ClientSecret' }
if ($missing.Count -gt 0) {
    Write-Host "smoke: missing required value(s): $($missing -join ' ')"
    Write-Host "Run 'Get-Help ./scripts/smoke.ps1 -Detailed' for usage."
    exit 2
}

$AuthUrl     = $AuthUrl.TrimEnd('/')
$TmsUrl      = $TmsUrl.TrimEnd('/')
$FrontendUrl = $FrontendUrl.TrimEnd('/')

$script:SkipCert   = [bool]$Insecure
$script:TimeoutSec = $TimeoutSec
$script:Pass = 0
$script:Fail = 0
$script:Warn = 0

function Add-Pass { param([string]$Message) $script:Pass++; Write-Host "  [ OK ] $Message" }
function Add-Fail { param([string]$Message) $script:Fail++; Write-Host "  [FAIL] $Message" }
function Add-Warn { param([string]$Message) $script:Warn++; Write-Host "  [WARN] $Message" }

# Single request primitive. -SkipHttpErrorCheck keeps 4xx/5xx as inspectable responses (PS 7+);
# transport failures (DNS/refused) still throw, so callers wrap in try/catch.
function Invoke-Smoke {
    param(
        [string]   $Url,
        [string]   $Method      = 'Get',
        $Body                    = $null,
        [string]   $ContentType  = $null,
        [hashtable]$Headers      = @{}
    )
    $params = @{
        Uri                = $Url
        Method             = $Method
        Headers            = $Headers
        SkipHttpErrorCheck = $true
        MaximumRedirection = 0
        TimeoutSec         = $script:TimeoutSec
    }
    if ($script:SkipCert) { $params['SkipCertificateCheck'] = $true }
    if ($null -ne $Body)  { $params['Body'] = $Body }
    if ($ContentType)     { $params['ContentType'] = $ContentType }
    return Invoke-WebRequest @params
}

function Get-Status {
    param([string]$Url, [hashtable]$Headers = @{})
    try { return [int](Invoke-Smoke -Url $Url -Headers $Headers).StatusCode }
    catch {
        # On older PS, a 3xx with MaximumRedirection=0 can throw despite -SkipHttpErrorCheck; recover
        # the status from the response so a redirecting endpoint reports its 30x (parity with the
        # bash twin, which never follows redirects), not 0. A genuine transport failure stays 0.
        $response = $_.Exception.Response
        if ($response -and $response.StatusCode) { return [int]$response.StatusCode }
        return 0
    }
}

Write-Host "== LotroKoniecDev post-deploy smoke test =="
Write-Host "Targets:"
Write-Host "  auth     = $AuthUrl"
Write-Host "  tms      = $TmsUrl"
Write-Host "  frontend = $FrontendUrl"
if ($Insecure) { Write-Host "  (TLS verification disabled: -Insecure)" }
Write-Host ""

Write-Host "[1/5] Health"
$code = Get-Status "$AuthUrl/health/ready"
if ($code -eq 200) { Add-Pass "auth /health/ready -> 200" } else { Add-Fail "auth /health/ready -> $code (expected 200)" }
$code = Get-Status "$TmsUrl/health/ready"
if ($code -eq 200) { Add-Pass "tms /health/ready -> 200" } else { Add-Fail "tms /health/ready -> $code (expected 200)" }
# Static-SSR app: no /health endpoint. 2xx (home) or 3xx (redirect to login) both mean up.
$code = Get-Status "$FrontendUrl/"
if ($code -ge 200 -and $code -lt 400) { Add-Pass "frontend / -> $code (serving)" } else { Add-Fail "frontend / -> $code (expected 2xx/3xx)" }
Write-Host ""

Write-Host "[2/5] Frontend static web assets + CSP consistency (#414, #670)"
$homeHtml = ''
$csp = ''
$homeResponse = $null
try {
    $homeResponse = Invoke-Smoke -Url "$FrontendUrl/"
    $homeHtml = $homeResponse.Content
} catch { $homeHtml = '' }
if ($null -eq $homeHtml) { $homeHtml = '' }
# Header extraction sits OUTSIDE that try on purpose: a throw in here must not blank an already
# fetched body, or the leg would report "static web assets did not ship" and roll the box back.
# HTTP/2 lower-cases header names, so match on the name instead of indexing by exact case.
if ($null -ne $homeResponse) {
    try {
        foreach ($headerName in $homeResponse.Headers.Keys) {
            if ($headerName -eq 'Content-Security-Policy') { $csp = ($homeResponse.Headers[$headerName] -join ' ') }
        }
    } catch { $csp = '' }
}
# @Assets["_framework/blazor.web.js"] renders a fingerprinted src ONLY when MapStaticAssets resolved
# the publish manifest. A bare `blazor.web.js` means the manifest shipped empty.
$assetMatch = [regex]::Match($homeHtml, '_framework/blazor\.web\.[A-Za-z0-9]+\.js')
if (-not $assetMatch.Success) {
    Add-Fail "frontend / has no fingerprinted _framework/blazor.web.<hash>.js (image built without its static web assets)"
} else {
    $assetPath = $assetMatch.Value
    Add-Pass "frontend / references $assetPath (manifest resolved)"
    $code = Get-Status "$FrontendUrl/$assetPath"
    if ($code -eq 200) { Add-Pass "frontend /$assetPath -> 200" } else { Add-Fail "frontend /$assetPath -> $code (expected 200)" }

    # Only runs once the fingerprint above proved the body is real HTML: an empty body has no inline
    # script either, so this check would otherwise go green exactly when the site is down.
    $scriptSrc = @($csp -split ';' | Where-Object { $_ -match 'script-src' }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($csp)) {
        # Development skips the whole security-headers middleware, so a local dev stack lands here.
        if ($RequireCsp) {
            Add-Fail "frontend / sends no Content-Security-Policy header (-RequireCsp)"
        } else {
            Add-Warn "frontend / sends no Content-Security-Policy header (expected only on a Development stack)"
        }
    } elseif ($scriptSrc -match 'unsafe-inline') {
        Add-Fail "frontend / script-src allows 'unsafe-inline' - injected script is no longer blocked (#670)"
    } elseif ($scriptSrc -match 'nonce-|sha(256|384|512)-') {
        # A nonce or hash is the documented way to allow one specific inline script, so the count below
        # would prove nothing.
        Add-Pass "frontend / script-src admits inline script only by nonce or hash"
    } else {
        # HTML tag and attribute names are case-insensitive, so match them that way ((?i), and
        # -notmatch is already case-insensitive). [^>] matches newlines, so a tag split over two
        # lines is still counted; the bash twin folds newlines first because grep works line by line.
        $scriptTags = [regex]::Matches($homeHtml, '(?i)<script[^>]*>')
        $inlineScripts = @($scriptTags | Where-Object { $_.Value -notmatch 'src=' }).Count
        if ($inlineScripts -eq 0) {
            Add-Pass "frontend / serves no inline <script> (nothing for script-src to block)"
        } else {
            Add-Fail "frontend / serves $inlineScripts inline <script> element(s), which its own script-src blocks (#670)"
        }
    }
}
Write-Host ""

Write-Host "[3/5] OIDC token round-trip (client_credentials)"
$token = $null
$tokenCode = 0
try {
    $resp = Invoke-Smoke -Url "$AuthUrl/connect/token" -Method 'Post' -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type    = 'client_credentials'
        client_id     = $ClientId
        client_secret = $ClientSecret
        scope         = $Scope
    }
    $tokenCode = [int]$resp.StatusCode
    if ($tokenCode -eq 200) { $token = ($resp.Content | ConvertFrom-Json).access_token }
} catch { $tokenCode = 0 }
if ($tokenCode -eq 200 -and $token) {
    Add-Pass "POST auth/connect/token -> 200, access_token received (client '$ClientId', scope '$Scope')"
} else {
    Add-Fail "POST auth/connect/token -> $tokenCode, no access_token (check client id/secret + scope grant)"
}
Write-Host ""

Write-Host "[4/5] Token accepted by tms-api (authenticated read)"
$anonCode = Get-Status "$TmsUrl/api/v1/game-versions"
if ($anonCode -eq 401) { Add-Pass "GET tms/api/v1/game-versions (no token) -> 401 (protected)" }
else { Add-Fail "GET tms/api/v1/game-versions (no token) -> $anonCode (expected 401)" }
if ($token) {
    $authCode = Get-Status "$TmsUrl/api/v1/game-versions" @{ Authorization = "Bearer $token" }
    switch ($authCode) {
        { $_ -in 200, 403 } { Add-Pass "GET tms/api/v1/game-versions (bearer) -> $authCode (token validated by tms)" }
        401 { Add-Fail "GET tms/api/v1/game-versions (bearer) -> 401 TOKEN REJECTED — issuer/audience/JWKS mismatch (runbook: Consistency rules #1/#2)" }
        default { Add-Fail "GET tms/api/v1/game-versions (bearer) -> $authCode (expected 200/403)" }
    }
} else {
    Add-Fail "GET tms/api/v1/game-versions (bearer) -> skipped, no access token from step 2"
}
Write-Host ""

Write-Host "[5/5] Translation-file distribution (ETag / 304)"
$fileCode = 0
$etag = $null
try {
    $resp = Invoke-Smoke -Url "$TmsUrl/api/v1/translation-files/$Lang"
    $fileCode = [int]$resp.StatusCode
    if ($resp.Headers.ContainsKey('ETag')) {
        $etag = $resp.Headers['ETag']
        if ($etag -is [array]) { $etag = $etag[0] }
    }
} catch { $fileCode = 0 }
if ($fileCode -eq 200) {
    if ($etag) {
        Add-Pass "GET tms/api/v1/translation-files/$Lang -> 200, ETag $etag"
        $revalCode = Get-Status "$TmsUrl/api/v1/translation-files/$Lang" @{ 'If-None-Match' = $etag }
        if ($revalCode -eq 304) { Add-Pass "revalidate with If-None-Match -> 304 (not modified)" }
        else { Add-Fail "revalidate with If-None-Match -> $revalCode (expected 304)" }
    } else {
        Add-Fail "GET tms/api/v1/translation-files/$Lang -> 200 but no ETag header"
    }
} elseif ($fileCode -eq 404) {
    Add-Warn "GET tms/api/v1/translation-files/$Lang -> 404 (endpoint up, but no '$Lang' artifact built yet — import/seed has not run)"
} else {
    Add-Fail "GET tms/api/v1/translation-files/$Lang -> $fileCode (expected 200, or 404 if unseeded)"
}
Write-Host ""

Write-Host "=================================================="
if ($script:Fail -eq 0) {
    Write-Host "Result: PASSED — $($script:Pass) check(s) ok, $($script:Warn) warning(s)."
    exit 0
}
Write-Host "Result: FAILED — $($script:Fail) failure(s), $($script:Pass) ok, $($script:Warn) warning(s)."
exit 1
