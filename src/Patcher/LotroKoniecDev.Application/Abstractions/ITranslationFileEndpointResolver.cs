namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Finds the absolute URI the translation file is downloaded from, by link relation, in the TMS
/// service document. It never builds a path. The CLI runs on players' machines and we cannot update
/// it remotely, so a hardcoded route would bind us forever (ADR-0041, #611).
/// </summary>
public interface ITranslationFileEndpointResolver
{
    /// <summary>
    /// Tries discovery first. The last href that worked is used only when the server is unreachable.
    /// </summary>
    /// <param name="baseUrl">The only configured input: the TMS root URL.</param>
    /// <param name="cachedHref">The href an earlier successful sync stored, or <c>null</c>.</param>
    Task<Result<Uri>> ResolveAsync(string baseUrl, string? cachedHref, CancellationToken cancellationToken);
}
