namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// The caller owns <see cref="Content"/> and disposes it, which also ends the response.
/// </summary>
internal sealed record ApiBodyStream(Stream Content, long? Length);
