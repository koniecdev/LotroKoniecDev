namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;

/// <summary>
/// Minimal projection of the TMS API <c>GET /health</c> payload. The Frontend only needs the
/// aggregate <see cref="Status"/> (e.g. <c>Healthy</c>) to confirm the API is reachable; the
/// per-check breakdown the API also returns is intentionally not modelled here.
/// </summary>
internal sealed record HealthStatusResponse(string Status);
