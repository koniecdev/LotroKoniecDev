resource "azurerm_log_analytics_workspace" "law" {
  count               = local.create_env ? 1 : 0
  location            = azurerm_resource_group.main.location
  name                = "lotrotmslaw${var.env_id}"
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = 30

  # This one workspace ingests BOTH projects (lotrotms + tks, prod + staging) — they share the
  # `lotrotmsenvprod` Container Apps environment, so there is exactly one LAW in the subscription.
  #
  # 0.16 pinned ingestion to the 5 GB/month free grant (5 / 31 = 0.161) and was hit EVERY day around
  # 20:00 UTC, leaving the workspace blind from then until the 12:00 UTC reset — 16 h/day, and the
  # hole covers the 05:00 UTC cron warm-window scale-up (ADR-0027), which is precisely when the
  # cold-start replica restarts we need to diagnose happen. Raised to 0.5 to reopen that window.
  #
  # Raising the cap does not by itself cost anything: billing is per GB ingested, and the cap only
  # truncates. Measured burn is ~0.168 GB/day at 24/7 uptime and ~0.105 GB/day (~3.3 GB/month) since
  # scale-to-zero — still inside the free grant. ~65% of it is /health/live + /health/ready probe
  # telemetry; filtering those out of OpenTelemetry is the durable fix, after which this drops back.
  daily_quota_gb = 0.5

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