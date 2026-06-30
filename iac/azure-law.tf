resource "azurerm_log_analytics_workspace" "law" {
  location            = var.location
  name                = "lotrotmslaw${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = 0.16

  lifecycle {
    # Audit 0001 / H11 (ADR-0017): losing this workspace drops all log history. Guard against a stray
    # rename / -target slip during multi-env work.
    prevent_destroy = true
  }

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}