namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Resolves the absolute URI the translation file is downloaded from, by link relation, out of the
/// TMS service document — never by composing a path. The CLI ships to players' machines and cannot
/// be updated remotely, so a hardcoded route would be a permanent commitment (ADR-0041 / #611).
/// </summary>
public interface ITranslationFileEndpointResolver
{
    /// <summary>
    /// Discovery first, the last-known-good href only as an outage safety net.
    /// </summary>
    /// <param name="baseUrl">The single configured input: the TMS root URL.</param>
    /// <param name="cachedHref">The href a previous successful sync stored, or <c>null</c>.</param>
    Task<Result<Uri>> ResolveAsync(string baseUrl, string? cachedHref, CancellationToken cancellationToken);
}
