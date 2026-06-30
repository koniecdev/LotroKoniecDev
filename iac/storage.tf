resource "azurerm_storage_account" "keys" {
  name                     = "lotrotmskeys${var.env_id}"
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  min_tls_version          = "TLS1_2"

  lifecycle {
    # Audit 0001 / H11 (ADR-0017): the Data Protection keyring lives in this account — destroying it
    # logs every user out. Guard against a stray rename / -target slip during multi-env work.
    prevent_destroy = true
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

resource "azurerm_storage_share" "auth_keys" {
  name                 = "auth-keys"
  storage_account_name = azurerm_storage_account.keys.name
  quota                = 5
}

resource "azurerm_storage_share" "frontend_keys" {
  name                 = "frontend-keys"
  storage_account_name = azurerm_storage_account.keys.name
  quota                = 5
}

resource "azurerm_container_app_environment_storage" "auth_keys" {
  name                         = "auth-keys"
  container_app_environment_id = azurerm_container_app_environment.app_env.id
  account_name                 = azurerm_storage_account.keys.name
  share_name                   = azurerm_storage_share.auth_keys.name
  access_key                   = azurerm_storage_account.keys.primary_access_key
  access_mode                  = "ReadWrite"
}

resource "azurerm_container_app_environment_storage" "frontend_keys" {
  name                         = "frontend-keys"
  container_app_environment_id = azurerm_container_app_environment.app_env.id
  account_name                 = azurerm_storage_account.keys.name
  share_name                   = azurerm_storage_share.frontend_keys.name
  access_key                   = azurerm_storage_account.keys.primary_access_key
  access_mode                  = "ReadWrite"
}
