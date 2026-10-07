namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

/// <summary>
/// A response body read straight from the connection, and its length when the API sent one. The
/// caller owns <see cref="Content"/> and disposes it, which also ends the response.
/// </summary>
internal sealed record ApiBodyStream(Stream Content, long? Length);
