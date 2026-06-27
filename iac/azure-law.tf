resource "azurerm_log_analytics_workspace" "law" {
  location            = azurerm_resource_group.rg-lotrotms-dev-polc-001.location
  name                = "lotrotmslaw${var.env_id}"
  resource_group_name = azurerm_resource_group.rg-lotrotms-dev-polc-001.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = 0.16

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}