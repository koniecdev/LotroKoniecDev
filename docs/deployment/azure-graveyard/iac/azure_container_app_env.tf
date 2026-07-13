resource "azurerm_container_app_environment" "app_env" {
  count                      = local.create_env ? 1 : 0
  location                   = azurerm_resource_group.main.location
  name                       = "lotrotmsenv${var.env_id}"
  resource_group_name        = azurerm_resource_group.main.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.law[0].id

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# Shared-environment mode (M6-22): when var.aca_environment_name is set, this env deploys its apps
# into that EXISTING managed environment (the subscription caps Container App Environments at one,
# held by prod) instead of creating its own above. Read-only; surfaced to every app/job/storage via
# local.app_env_id.
data "azurerm_container_app_environment" "shared" {
  count               = local.create_env ? 0 : 1
  name                = var.aca_environment_name
  resource_group_name = var.aca_environment_resource_group
}

# M6-22: app_env gained a count for shared-environment mode, shifting its address to [0]. For prod
# (create_env = true) the resource is otherwise unchanged, so this is a pure state-address move
# (terraform plan shows a `moved`, never a destroy/recreate) — same pattern as resource-group.tf.
moved {
  from = azurerm_container_app_environment.app_env
  to   = azurerm_container_app_environment.app_env[0]
}
