#!/usr/bin/env bash
# Seeds Azure Key Vault as the single source of truth for the TMS production secrets (ADR-0013).
#
# Idempotent — safe to re-run for bring-up AND for rotation. Ensures, in order:
#   1. the Key Vault                    (RBAC-authorization mode, soft-delete on)
#   2. a user-assigned managed identity (the one the ACA apps + migrator job assume)
#   3. "Key Vault Secrets User"  on the Vault for that identity   (data-plane read at runtime)
#   4. "Key Vault Secrets Officer" on the Vault for the caller    (so this script can set secrets)
#   5. the 8 secret values
#
# Terraform (iac/) only data-references the Vault + identity and wires the ACA secret references;
# it never receives a plaintext value, so nothing secret rests on disk or in the Terraform state.
#
# Secret VALUES come from environment variables (never an argument, never a committed file):
#   SEED_CONNECTION_STRING_TRANSLATION  -> connection-string-translation
#   SEED_CONNECTION_STRING_AUTH         -> connection-string-auth
#   SEED_OPENIDDICT_SIGNING_KEY         -> openiddict-signing-key
#   SEED_OPENIDDICT_ENCRYPTION_KEY      -> openiddict-encryption-key
#   SEED_OPENIDDICT_API_CLIENT_SECRET   -> openiddict-api-client-secret
#   SEED_SMTP_USERNAME                  -> smtp-username
#   SEED_SMTP_PASSWORD                  -> smtp-password
#   SEED_ADMIN_PASSWORD                 -> admin-password
#
# Usage (one-time bring-up + every rotation):
#   az login                                       # an Owner / User Access Administrator on the sub
#   export SEED_CONNECTION_STRING_TRANSLATION=...   # … export all 8 SEED_* …
#   scripts/seed-keyvault.sh
#
# Overridable (defaults match iac/): KV_RESOURCE_GROUP, KV_LOCATION, KV_NAME, KV_IDENTITY_NAME.
# The PowerShell twin scripts/seed-keyvault.ps1 must stay in lock-step with this file.
#
# Requires: az (Azure CLI), logged in to the prod subscription.

set -euo pipefail

RESOURCE_GROUP="${KV_RESOURCE_GROUP:-rg-lotrotms-prod-polc-001}"
LOCATION="${KV_LOCATION:-polandcentral}"
VAULT_NAME="${KV_NAME:-lotrotms-kv-prod}"
IDENTITY_NAME="${KV_IDENTITY_NAME:-lotrotms-aca-prod}"

# KV secret name  <-  SEED_* env var holding its value. Keep the names in sync with the ACA
# `secret { name = ... }` blocks in iac/azure-container-apps.tf + iac/migrator-job.tf.
SECRETS=(
  "connection-string-translation:SEED_CONNECTION_STRING_TRANSLATION"
  "connection-string-auth:SEED_CONNECTION_STRING_AUTH"
  "openiddict-signing-key:SEED_OPENIDDICT_SIGNING_KEY"
  "openiddict-encryption-key:SEED_OPENIDDICT_ENCRYPTION_KEY"
  "openiddict-api-client-secret:SEED_OPENIDDICT_API_CLIENT_SECRET"
  "smtp-username:SEED_SMTP_USERNAME"
  "smtp-password:SEED_SMTP_PASSWORD"
  "admin-password:SEED_ADMIN_PASSWORD"
)

die() {
  echo "ERROR: $*" >&2
  exit 1
}

command -v az >/dev/null 2>&1 || die "Azure CLI (az) is not installed."
az account show >/dev/null 2>&1 || die "Not logged in — run 'az login' against the prod subscription."

missing=()
for pair in "${SECRETS[@]}"; do
  var="${pair#*:}"
  [ -n "${!var:-}" ] || missing+=("$var")
done
[ "${#missing[@]}" -eq 0 ] || die "Missing secret env var(s): ${missing[*]}"

echo "==> Subscription: $(az account show --query name -o tsv) ($(az account show --query id -o tsv))"
echo "==> Resource group: ${RESOURCE_GROUP} | Vault: ${VAULT_NAME} | Identity: ${IDENTITY_NAME}"

# 1. Key Vault (idempotent: create only if absent). RBAC authorization is the current best practice.
if az keyvault show -n "$VAULT_NAME" -g "$RESOURCE_GROUP" >/dev/null 2>&1; then
  echo "==> Vault ${VAULT_NAME} already exists — skipping create."
else
  echo "==> Creating Vault ${VAULT_NAME} …"
  az keyvault create \
    -n "$VAULT_NAME" \
    -g "$RESOURCE_GROUP" \
    -l "$LOCATION" \
    --sku standard \
    --enable-rbac-authorization true \
    --retention-days 90 \
    -o none
fi
VAULT_ID="$(az keyvault show -n "$VAULT_NAME" -g "$RESOURCE_GROUP" --query id -o tsv)"

# 2. User-assigned managed identity (idempotent).
if az identity show -n "$IDENTITY_NAME" -g "$RESOURCE_GROUP" >/dev/null 2>&1; then
  echo "==> Identity ${IDENTITY_NAME} already exists — skipping create."
else
  echo "==> Creating user-assigned identity ${IDENTITY_NAME} …"
  az identity create -n "$IDENTITY_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION" -o none
fi
IDENTITY_PRINCIPAL_ID="$(az identity show -n "$IDENTITY_NAME" -g "$RESOURCE_GROUP" --query principalId -o tsv)"
IDENTITY_RESOURCE_ID="$(az identity show -n "$IDENTITY_NAME" -g "$RESOURCE_GROUP" --query id -o tsv)"

# 3 + 4. Role assignments (idempotent — az is a no-op when the assignment already exists).
echo "==> Granting 'Key Vault Secrets User' to the identity …"
az role assignment create \
  --assignee-object-id "$IDENTITY_PRINCIPAL_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" \
  --scope "$VAULT_ID" \
  -o none

CALLER_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)"
if [ -n "$CALLER_OBJECT_ID" ]; then
  echo "==> Granting 'Key Vault Secrets Officer' to the caller (to set secrets) …"
  az role assignment create \
    --assignee-object-id "$CALLER_OBJECT_ID" \
    --assignee-principal-type User \
    --role "Key Vault Secrets Officer" \
    --scope "$VAULT_ID" \
    -o none
else
  echo "WARNING: could not resolve the caller object id (service principal?). Ensure the caller holds" >&2
  echo "         'Key Vault Secrets Officer' on ${VAULT_NAME} before secrets can be set." >&2
fi

# 5. Set the secrets. Data-plane RBAC can take a minute to propagate after the role assignment above,
#    so the first set is retried until it is authorized.
set_secret() {
  local name="$1" value="$2" attempt
  for attempt in $(seq 1 18); do
    if az keyvault secret set --vault-name "$VAULT_NAME" --name "$name" --value "$value" -o none 2>/dev/null; then
      return 0
    fi
    echo "   …waiting for data-plane RBAC to propagate (attempt ${attempt}) …"
    sleep 10
  done
  die "Could not set secret '${name}' — the caller still lacks data-plane access on ${VAULT_NAME}."
}

for pair in "${SECRETS[@]}"; do
  name="${pair%%:*}"
  var="${pair#*:}"
  echo "==> Setting secret ${name} …"
  set_secret "$name" "${!var}"
done

echo ""
echo "Done. Key Vault is seeded."
echo "  key_vault_name    = ${VAULT_NAME}"
echo "  aca_identity_name = ${IDENTITY_NAME}"
echo "  identity id       = ${IDENTITY_RESOURCE_ID}"
echo "Terraform reads these via the data sources in iac/keyvault.tf (defaults already match)."
