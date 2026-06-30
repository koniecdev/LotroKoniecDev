resource "azurerm_log_analytics_workspace" "law" {
  count               = local.create_env ? 1 : 0
  location            = azurerm_resource_group.main.location
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

# M6-22: law gained a count for shared-environment mode (staging reuses prod's workspace), shifting
# its address to [0]. For prod (create_env = true) the workspace is otherwise unchanged, so this is a
# pure state-address move — never a destroy/recreate.
moved {
  from = azurerm_log_analytics_workspace.law
  to   = azurerm_log_analytics_workspace.law[0]
}