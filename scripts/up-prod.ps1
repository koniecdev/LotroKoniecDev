# "docker compose up" for the production-parity stack, with one-time .env.prod + TLS bootstrap.
# Mirrors scripts/up.ps1 but targets compose.prod.yaml. On first run it creates .env.prod from the
# example WITH freshly generated OpenIddict secrets, and mints the local CA / proxy / Postgres certs.
# Review the SMTP / admin / database values in .env.prod before a real run. Extra args pass through.

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $repoRoot ".env.prod"
$certPath = Join-Path $repoRoot ".docker/prod-https/proxy.crt"

Set-Location $repoRoot

if (-not (Test-Path $envFile))
{
    # Drop the three empty OpenIddict placeholders, then append freshly generated secrets.
    Get-Content (Join-Path $repoRoot ".env.prod.example") |
        Where-Object { $_ -notmatch '^OpenIddict__(EncryptionKey__Key|SigningKey__RsaPrivateKeyXml|ApiClientSecret)=' } |
        Set-Content $envFile
    & (Join-Path $PSScriptRoot "gen-openiddict-keys.ps1") | Add-Content $envFile
    Write-Host ".env.prod created with generated OpenIddict secrets. Review SMTP/admin/DB values before prod use."
}

if (-not (Test-Path $certPath))
{
    Write-Host "Prod-parity TLS material missing - running one-time bootstrap..."
    & (Join-Path $PSScriptRoot "init-prod-https.ps1")
}

# Map the *.lotro.test vhosts to loopback (REQUIRED — *.test is never resolved by public DNS, by
# design, so only a local hosts entry works). Idempotent: a no-op once present, so no repeated
# prompts — admin is needed only the first time on a fresh machine. The in-stack containers don't
# need this (they reach Caddy via Docker network aliases); it's purely for the browser + host curl.
$vhosts = @('app.lotro.test', 'auth.lotro.test', 'tms.lotro.test')
if ($IsWindows -or ($null -eq $IsWindows -and $env:OS -eq 'Windows_NT'))
{
    $hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
}
else
{
    $hostsPath = '/etc/hosts'
}

$hostsContent = if (Test-Path $hostsPath) { Get-Content $hostsPath -Raw -ErrorAction SilentlyContinue } else { '' }
$hostsMissing = $false
foreach ($name in $vhosts) { if ($hostsContent -notmatch [regex]::Escape($name)) { $hostsMissing = $true } }

if ($hostsMissing)
{
    Write-Host "Mapping *.lotro.test -> 127.0.0.1 in $hostsPath (one-time per machine; may prompt for admin)..."
    $hostsValue = "`n# LotroKoniecDev prod-parity (compose.prod.yaml) - auto-added by up-prod`n127.0.0.1 $($vhosts -join ' ')"
    try
    {
        Add-Content -Path $hostsPath -Value $hostsValue -ErrorAction Stop
    }
    catch
    {
        # Not writable as the current user — elevate just for the append (UAC on Windows, sudo on *nix).
        $escaped = $hostsValue -replace "'", "''"
        if ($hostsPath -like '*System32*')
        {
            Start-Process -FilePath 'powershell' -Verb RunAs -Wait `
                -ArgumentList '-NoProfile', '-Command', "Add-Content -Path '$hostsPath' -Value '$escaped'"
        }
        else
        {
            & sudo pwsh -NoProfile -Command "Add-Content -Path '$hostsPath' -Value '$escaped'"
        }
    }
}

docker compose -f compose.prod.yaml --env-file $envFile up @args
