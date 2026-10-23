using LotroKoniecDev.Frontend.Infrastructure.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.Frontend.Components.Pages.ImportExport;

/// <summary>
/// Which versions an <c>exported.txt</c> can go into, and where, from the per-item <c>import</c> rel only
/// (#610). The API leaves it out for a non-admin and for a <c>Superseded</c> version, so no status is
/// read here and no URL is built from an id. A class of its own because bUnit cannot send a file input
/// through static SSR form binding, so the page's form cannot test this rule.
/// </summary>
internal static class ImportTargets
{
    internal static IReadOnlyList<GameVersionResponse> Importable(IReadOnlyList<GameVersionResponse>? versions) =>
        versions is null
            ? []
            : [.. versions.Where(version => version.Links.HasLink(Rels.Import))];

    /// <summary>
    /// <see langword="null"/> when the version is unknown (the list changed under an old form post) or
    /// offers no import. Then we do not call, and there is no fallback.
    /// </summary>
    internal static string? FindImportHref(IReadOnlyList<GameVersionResponse>? versions, Guid gameVersionId) =>
        versions
            ?.FirstOrDefault(version => version.Id.Value == gameVersionId)
            ?.Links.FindLink(Rels.Import)
            ?.Href;
}
