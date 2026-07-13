resource "azurerm_resource_group" "main" {
  location = "polandcentral"
  name     = "rg-lotrotms-${var.env_id}-polc-001"

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}

# Audit 0001 / H2 (ADR-0017): the symbol was renamed rg-lotrotms-prod-polc-001 -> main and the name is
# now env_id-derived. For env_id = "prod" the name is unchanged, so this is a pure state-address move
# (terraform plan shows a `moved`, never a destroy/recreate).
moved {
  from = azurerm_resource_group.rg-lotrotms-prod-polc-001
  to   = azurerm_resource_group.main
}
