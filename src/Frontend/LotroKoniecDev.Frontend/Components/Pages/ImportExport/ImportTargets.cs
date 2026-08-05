using LotroKoniecDev.Frontend.Infrastructure.Hateoas;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.Frontend.Components.Pages.ImportExport;

/// <summary>
/// Which game versions an <c>exported.txt</c> may actually be imported into, and where to POST it —
/// read purely from the per-item <c>import</c> rel the API emits (#610). The API withholds that rel for
/// a non-admin and for a <c>Superseded</c> version, so this is the whole gate: no status enum is
/// re-interpreted here and no URI is composed from an id.
/// <para>
/// Pure and isolated because the page's own form cannot be driven end-to-end in bUnit — Blazor's
/// static-SSR form mapping does not serialize a programmatically set file input — so the selection rule
/// would otherwise ship unverified (mirrors <see cref="Editor.PlaceholderAnalyzer"/>).
/// </para>
/// </summary>
internal static class ImportTargets
{
    /// <summary>
    /// The versions to offer in the selector: only those advertising <c>import</c>. Offering a version
    /// the server would refuse is a lie the user only discovers on submit.
    /// </summary>
    internal static IReadOnlyList<GameVersionResponse> Importable(IReadOnlyList<GameVersionResponse>? versions) =>
        versions is null
            ? []
            : [.. versions.Where(version => version.Links.HasLink(Rels.Import))];

    /// <summary>
    /// The <c>import</c> href advertised by the version <paramref name="gameVersionId"/> identifies, or
    /// <see langword="null"/> when that version is unknown (the list moved under a stale form post) or
    /// offers no import affordance. A <see langword="null"/> is a refusal to call, never a fallback.
    /// </summary>
    internal static string? FindImportHref(IReadOnlyList<GameVersionResponse>? versions, Guid gameVersionId) =>
        versions
            ?.FirstOrDefault(version => version.Id.Value == gameVersionId)
            ?.Links.FindLink(Rels.Import)
            ?.Href;
}
