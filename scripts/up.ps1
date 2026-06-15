# "docker compose up" with one-time .env + dev-cert bootstrap baked in.
# compose has no secret defaults, so a .env is required; this creates it from .env.example
# on first run (edit secrets there afterwards). Runs init-dev-https.ps1 if the HTTPS dev-cert
# PFX is missing, then `docker compose up`. Extra args pass through.

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $repoRoot ".env"
$certPath = Join-Path $repoRoot ".docker/https/aspnetapp.pfx"

if (-not (Test-Path $envFile))
{
    Copy-Item (Join-Path $repoRoot ".env.example") $envFile
    Write-Host ".env created from .env.example. Edit secrets if needed."
}

if (-not (Test-Path $certPath))
{
    Write-Host "Dev HTTPS cert missing - running one-time bootstrap..."
    & (Join-Path $PSScriptRoot "init-dev-https.ps1")
}

Set-Location $repoRoot
docker compose up @args
