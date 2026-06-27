resource "azurerm_resource_group" "rg-lotrotms-prod-polc-001" {
  location = "polandcentral"
  name     = "rg-lotrotms-prod-polc-001"

  tags = {
    environment = var.env_id
    src         = var.src_key
    project     = "lotrotms"
  }
}