using LotroKoniecDev.Frontend.Infrastructure.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.Frontend.Components.Pages.ImportExport;

/// <summary>
/// Which game versions an <c>exported.txt</c> can be imported into, and where to send it. Both come only
/// from the per-item <c>import</c> rel the API sends (#610). The API leaves that rel out for anyone who
/// is not an admin and for a <c>Superseded</c> version, so this is the whole check: no status is
/// interpreted here and no URL is built from an id.
/// <para>
/// It sits in its own class because the page's form cannot be driven end to end in bUnit: Blazor's
/// static SSR form binding does not send a file input set from code. Without this the selection rule
/// would ship untested, like <see cref="Editor.PlaceholderAnalyzer"/>.
/// </para>
/// </summary>
internal static class ImportTargets
{
    /// <summary>
    /// The versions to show in the selector: only those that offer <c>import</c>. Offering a version the
    /// server would refuse only misleads the user until they press submit.
    /// </summary>
    internal static IReadOnlyList<GameVersionResponse> Importable(IReadOnlyList<GameVersionResponse>? versions) =>
        versions is null
            ? []
            : [.. versions.Where(version => version.Links.HasLink(Rels.Import))];

    /// <summary>
    /// The <c>import</c> href of the version <paramref name="gameVersionId"/> names, or
    /// <see langword="null"/> when that version is unknown, because the list changed under an old form
    /// post, or when it offers no import. A <see langword="null"/> means we do not call, never that we
    /// fall back to something else.
    /// </summary>
    internal static string? FindImportHref(IReadOnlyList<GameVersionResponse>? versions, Guid gameVersionId) =>
        versions
            ?.FirstOrDefault(version => version.Id.Value == gameVersionId)
            ?.Links.FindLink(Rels.Import)
            ?.Href;
}
