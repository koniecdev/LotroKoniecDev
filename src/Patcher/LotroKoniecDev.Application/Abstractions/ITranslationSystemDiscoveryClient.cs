namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Reads the TMS service document, the anonymous discovery root at the API's base URL, and returns
/// the links it offers a caller who is not logged in. There is no API gateway (ADR-0041), so this is
/// the only place the CLI learns where anything lives.
/// </summary>
public interface ITranslationSystemDiscoveryClient
{
    Task<Result<IReadOnlyList<DiscoveredLink>>> FetchLinksAsync(string baseUrl, CancellationToken cancellationToken);
}

/// <summary>One link from a service document: what it is (<see cref="Rel"/>) and where it points.</summary>
public sealed record DiscoveredLink(string Href, string Rel, string Method);
