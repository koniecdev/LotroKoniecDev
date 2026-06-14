# "docker compose up" with a one-time .env bootstrap.
# The stack also boots fine without a .env (compose has sane defaults); this is
# purely a convenience so you can edit secrets in one place. Extra args pass through.

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
