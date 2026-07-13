#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Seeds Azure Key Vault as the single source of truth for the TMS production secrets (ADR-0013).
    PowerShell twin of scripts/seed-keyvault.sh — keep the two in lock-step.

.DESCRIPTION
    Idempotent — safe to re-run for bring-up AND for rotation. Ensures, in order:
      1. the Key Vault                    (RBAC-authorization mode, soft-delete on)
      2. a user-assigned managed identity (the one the ACA apps + migrator job assume)
      3. "Key Vault Secrets User"   on the Vault for that identity   (data-plane read at runtime)
      4. "Key Vault Secrets Officer" on the Vault for the caller     (so this script can set secrets)
      5. the 8 secret values

    Terraform (iac/) only data-references the Vault + identity and wires the ACA secret references;
    it never receives a plaintext value, so nothing secret rests on disk or in the Terraform state.

    Secret VALUES come from environment variables (never an argument, never a committed file):
      SEED_CONNECTION_STRING_TRANSLATION  -> connection-string-translation
      SEED_CONNECTION_STRING_AUTH         -> connection-string-auth
      SEED_OPENIDDICT_SIGNING_KEY         -> openiddict-signing-key
      SEED_OPENIDDICT_ENCRYPTION_KEY      -> openiddict-encryption-key
      SEED_OPENIDDICT_API_CLIENT_SECRET   -> openiddict-api-client-secret
      SEED_SMTP_USERNAME                  -> smtp-username
      SEED_SMTP_PASSWORD                  -> smtp-password
      SEED_ADMIN_PASSWORD                 -> admin-password

    Usage (one-time bring-up + every rotation):
      az login                                       # an Owner / User Access Administrator on the sub
      $env:SEED_CONNECTION_STRING_TRANSLATION = '…'   # … set all 8 SEED_* …
      ./scripts/seed-keyvault.ps1

    Overridable (defaults match iac/): KV_RESOURCE_GROUP, KV_LOCATION, KV_NAME, KV_IDENTITY_NAME.

    Requires: az (Azure CLI), logged in to the prod subscription.
#>

$ErrorActionPreference = 'Stop'

$ResourceGroup = if ($env:KV_RESOURCE_GROUP) { $env:KV_RESOURCE_GROUP } else { 'rg-lotrotms-prod-polc-001' }
$Location      = if ($env:KV_LOCATION)       { $env:KV_LOCATION }       else { 'polandcentral' }
$VaultName     = if ($env:KV_NAME)           { $env:KV_NAME }           else { 'lotrotms-kv-prod' }
$IdentityName  = if ($env:KV_IDENTITY_NAME)  { $env:KV_IDENTITY_NAME }  else { 'lotrotms-aca-prod' }

# KV secret name -> SEED_* env var holding its value. Keep the names in sync with the ACA
# `secret { name = ... }` blocks in iac/azure-container-apps.tf + iac/migrator-job.tf.
$Secrets = [ordered]@{
    'connection-string-translation' = 'SEED_CONNECTION_STRING_TRANSLATION'
    'connection-string-auth'        = 'SEED_CONNECTION_STRING_AUTH'
    'openiddict-signing-key'        = 'SEED_OPENIDDICT_SIGNING_KEY'
    'openiddict-encryption-key'     = 'SEED_OPENIDDICT_ENCRYPTION_KEY'
    'openiddict-api-client-secret'  = 'SEED_OPENIDDICT_API_CLIENT_SECRET'
    'smtp-username'                 = 'SEED_SMTP_USERNAME'
    'smtp-password'                 = 'SEED_SMTP_PASSWORD'
    'admin-password'                = 'SEED_ADMIN_PASSWORD'
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw 'Azure CLI (az) is not installed.' }
az account show 1>$null 2>$null
if ($LASTEXITCODE -ne 0) { throw "Not logged in — run 'az login' against the prod subscription." }

$missing = $Secrets.Values | Where-Object { -not [Environment]::GetEnvironmentVariable($_) }
if ($missing) { throw "Missing secret env var(s): $($missing -join ', ')" }

Write-Host "==> Subscription: $(az account show --query name -o tsv) ($(az account show --query id -o tsv))"
Write-Host "==> Resource group: $ResourceGroup | Vault: $VaultName | Identity: $IdentityName"

# 1. Key Vault (idempotent). RBAC authorization is the current best practice.
az keyvault show -n $VaultName -g $ResourceGroup 1>$null 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "==> Vault $VaultName already exists — skipping create."
}
else {
    Write-Host "==> Creating Vault $VaultName …"
    az keyvault create -n $VaultName -g $ResourceGroup -l $Location --sku standard `
        --enable-rbac-authorization true --retention-days 90 -o none
}
$VaultId = az keyvault show -n $VaultName -g $ResourceGroup --query id -o tsv

# 2. User-assigned managed identity (idempotent).
az identity show -n $IdentityName -g $ResourceGroup 1>$null 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "==> Identity $IdentityName already exists — skipping create."
}
else {
    Write-Host "==> Creating user-assigned identity $IdentityName …"
    az identity create -n $IdentityName -g $ResourceGroup -l $Location -o none
}
$IdentityPrincipalId = az identity show -n $IdentityName -g $ResourceGroup --query principalId -o tsv
$IdentityResourceId  = az identity show -n $IdentityName -g $ResourceGroup --query id -o tsv

# 3 + 4. Role assignments (idempotent — az is a no-op when the assignment already exists).
Write-Host "==> Granting 'Key Vault Secrets User' to the identity …"
az role assignment create --assignee-object-id $IdentityPrincipalId --assignee-principal-type ServicePrincipal `
    --role 'Key Vault Secrets User' --scope $VaultId -o none

$CallerObjectId = az ad signed-in-user show --query id -o tsv 2>$null
if ($CallerObjectId) {
    Write-Host "==> Granting 'Key Vault Secrets Officer' to the caller (to set secrets) …"
    az role assignment create --assignee-object-id $CallerObjectId --assignee-principal-type User `
        --role 'Key Vault Secrets Officer' --scope $VaultId -o none
}
else {
    Write-Warning "Could not resolve the caller object id (service principal?). Ensure the caller holds 'Key Vault Secrets Officer' on $VaultName before secrets can be set."
}

# 5. Set the secrets. Data-plane RBAC can take a minute to propagate after the role assignment above,
#    so the first set is retried until it is authorized.
function Set-KvSecret([string]$Name, [string]$Value) {
    foreach ($attempt in 1..18) {
        az keyvault secret set --vault-name $VaultName --name $Name --value $Value -o none 2>$null
        if ($LASTEXITCODE -eq 0) { return }
        Write-Host "   …waiting for data-plane RBAC to propagate (attempt $attempt) …"
        Start-Sleep -Seconds 10
    }
    throw "Could not set secret '$Name' — the caller still lacks data-plane access on $VaultName."
}

foreach ($name in $Secrets.Keys) {
    $value = [Environment]::GetEnvironmentVariable($Secrets[$name])
    Write-Host "==> Setting secret $name …"
    Set-KvSecret $name $value
}

Write-Host ''
Write-Host 'Done. Key Vault is seeded.'
Write-Host "  key_vault_name    = $VaultName"
Write-Host "  aca_identity_name = $IdentityName"
Write-Host "  identity id       = $IdentityResourceId"
Write-Host 'Terraform reads these via the data sources in iac/keyvault.tf (defaults already match).'
