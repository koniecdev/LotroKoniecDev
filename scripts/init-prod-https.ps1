#!/usr/bin/env pwsh
<#
.SYNOPSIS
    One-time bootstrap of the TLS material for the production-parity stack (compose.prod.yaml).

.DESCRIPTION
    Mints a local CA and the certs the prod-parity stack needs (the ASP.NET `localhost` dev cert
    does not cover the proxy vhosts). Everything lands in .docker/prod-https/ (git-ignored):
      * proxy.{crt,key}    leaf for app/auth/tms.lotro.test (mounted into Caddy)
      * rootCA.crt         the CA the Frontend + tms-api containers trust; trust-ca-entrypoint.sh
                           installs it into their OS trust store (.NET ignores SSL_CERT_FILE)
      * postgres.{crt,key} self-signed server cert so Postgres can run with ssl=on

    Uses .NET certificate APIs (no openssl dependency). Re-running is a no-op unless -Force.
    Add the hostnames to your hosts file once:
      127.0.0.1 app.lotro.test auth.lotro.test tms.lotro.test
#>

param([switch]$Force)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$certDir  = Join-Path $repoRoot '.docker/prod-https'

if ((Test-Path (Join-Path $certDir 'proxy.crt')) -and -not $Force) {
    Write-Host "Prod-parity TLS material already present in $certDir (use -Force to regenerate)."
    exit 0
}

New-Item -ItemType Directory -Force -Path $certDir | Out-Null

$notBefore = [System.DateTimeOffset]::UtcNow.AddDays(-1)
$sha = [System.Security.Cryptography.HashAlgorithmName]::SHA256
$pad = [System.Security.Cryptography.RSASignaturePadding]::Pkcs1

function New-SerialNumber {
    $b = [byte[]]::new(16)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($b)
    return $b
}

Write-Host "Generating local CA..."
$caRsa = [System.Security.Cryptography.RSA]::Create(2048)
$caReq = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=LotroKoniecDev Local Prod-Parity CA', $caRsa, $sha, $pad)
$caReq.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
$caCert = $caReq.CreateSelfSigned($notBefore, $notBefore.AddYears(10))

Write-Host "Generating reverse-proxy leaf cert (app/auth/tms.lotro.test)..."
$leafRsa = [System.Security.Cryptography.RSA]::Create(2048)
$leafReq = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=app.lotro.test', $leafRsa, $sha, $pad)
$leafReq.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
$san.AddDnsName('app.lotro.test'); $san.AddDnsName('auth.lotro.test'); $san.AddDnsName('tms.lotro.test')
$leafReq.CertificateExtensions.Add($san.Build())
$eku = [System.Security.Cryptography.OidCollection]::new()
$null = $eku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
$leafReq.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))
$leafCert = $leafReq.Create($caCert, $notBefore, $notBefore.AddDays(825), (New-SerialNumber))

Write-Host "Generating self-signed Postgres server cert..."
$pgRsa = [System.Security.Cryptography.RSA]::Create(2048)
$pgReq = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=lotro-postgres', $pgRsa, $sha, $pad)
$pgReq.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$pgCert = $pgReq.CreateSelfSigned($notBefore, $notBefore.AddDays(825))

Set-Content -Path (Join-Path $certDir 'rootCA.crt')  -Value $caCert.ExportCertificatePem()
Set-Content -Path (Join-Path $certDir 'proxy.crt')   -Value $leafCert.ExportCertificatePem()
Set-Content -Path (Join-Path $certDir 'proxy.key')   -Value $leafRsa.ExportPkcs8PrivateKeyPem()
Set-Content -Path (Join-Path $certDir 'postgres.crt') -Value $pgCert.ExportCertificatePem()
Set-Content -Path (Join-Path $certDir 'postgres.key') -Value $pgRsa.ExportPkcs8PrivateKeyPem()

Write-Host ""
Write-Host "Done. TLS material at: $certDir"
Write-Host "Ensure your hosts file maps the vhosts to loopback (one-time):"
Write-Host "  127.0.0.1 app.lotro.test auth.lotro.test tms.lotro.test"
