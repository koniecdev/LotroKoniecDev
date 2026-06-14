# "docker compose up" with a one-time .env bootstrap.
# compose has no secret defaults, so a .env is required; this creates it from .env.example
# on first run (edit secrets there afterwards). Extra args pass through.

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $repoRoot ".env"

if (-not (Test-Path $envFile))
{
    Copy-Item (Join-Path $repoRoot ".env.example") $envFile
    Write-Host ".env created from .env.example. Edit secrets if needed."
}

Set-Location $repoRoot
docker compose up @args
