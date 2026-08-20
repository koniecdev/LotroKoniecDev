namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Reads the TMS service document, the discovery root the API serves at its base URL for anyone, and
/// returns the links it offers to a caller who is not logged in. There is no API gateway (ADR-0041):
/// the discovery document is the contract, and this is the only place the CLI learns where anything
/// lives.
/// </summary>
public interface ITranslationSystemDiscoveryClient
{
    Task<Result<IReadOnlyList<DiscoveredLink>>> FetchLinksAsync(string baseUrl, CancellationToken cancellationToken);
}

/// <summary>One link from a service document: what it is (<see cref="Rel"/>) and where it points.</summary>
public sealed record DiscoveredLink(string Href, string Rel, string Method);
