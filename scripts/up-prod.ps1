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

Write-Host ""
Write-Host "Reminder: map the vhosts to loopback once (REQUIRED — *.test is not auto-resolved by browsers):"
Write-Host "  127.0.0.1 app.lotro.test auth.lotro.test tms.lotro.test"
Write-Host ""

docker compose -f compose.prod.yaml --env-file $envFile up @args
