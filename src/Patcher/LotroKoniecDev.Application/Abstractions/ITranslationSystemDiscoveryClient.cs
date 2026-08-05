namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Reads the TMS service document — the anonymous discovery root the API serves at its base URL —
/// and hands back the hypermedia links it advertises to an unauthenticated caller. There is no API
/// gateway (ADR-0041): the discovery document IS the client contract surface, so this is the only
/// place the CLI learns where anything lives.
/// </summary>
public interface ITranslationSystemDiscoveryClient
{
    Task<Result<IReadOnlyList<DiscoveredLink>>> FetchLinksAsync(string baseUrl, CancellationToken cancellationToken);
}

/// <summary>One hypermedia link from a service document: what it is (<see cref="Rel"/>) and where it lives.</summary>
public sealed record DiscoveredLink(string Href, string Rel, string Method);
