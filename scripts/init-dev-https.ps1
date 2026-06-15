#!/usr/bin/env pwsh
<#
.SYNOPSIS
    One-time bootstrap of the ASP.NET Core dev HTTPS certificate for Docker.

.DESCRIPTION
    Trusts the dev cert on the host (so the browser accepts https://localhost:*),
    then exports it as a PFX into ./.docker/https/aspnetapp.pfx so containers can
    mount it and serve HTTPS through Kestrel.

    Cert password is read from .env (key: ASPNETCORE_KESTREL_CERT_PASSWORD).
    Run once on a fresh clone, then `docker compose up`.
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$certDir  = Join-Path $repoRoot '.docker/https'
$certPath = Join-Path $certDir  'aspnetapp.pfx'
$envFile  = Join-Path $repoRoot '.env'

if (-not (Test-Path $envFile)) {
    Write-Error ".env not found. Copy .env.example to .env first."
    exit 1
}

$line = Get-Content $envFile |
    Where-Object { $_ -match '^\s*ASPNETCORE_KESTREL_CERT_PASSWORD\s*=\s*(.+)\s*$' } |
    Select-Object -First 1

if (-not $line) {
    Write-Error "ASPNETCORE_KESTREL_CERT_PASSWORD not set in .env"
    exit 1
}

$password = ($line -split '=', 2)[1].Trim()

New-Item -ItemType Directory -Force -Path $certDir | Out-Null

Write-Host "Trusting ASP.NET Core dev certificate (you may be prompted)..."
& dotnet dev-certs https --trust
if ($LASTEXITCODE -ne 0) { throw "dotnet dev-certs https --trust failed" }

if (Test-Path $certPath) { Remove-Item $certPath -Force }

Write-Host "Exporting PFX to $certPath ..."
& dotnet dev-certs https -ep $certPath -p $password --format Pfx | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet dev-certs https export failed" }

Write-Host ""
Write-Host "Done. Cert at: $certPath"
Write-Host "Next: docker compose up"
