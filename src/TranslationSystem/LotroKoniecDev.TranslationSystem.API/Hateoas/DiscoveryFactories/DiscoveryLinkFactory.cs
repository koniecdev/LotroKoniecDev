using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.LinkFactories;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Features.GameVersions;
using LotroKoniecDev.TranslationSystem.API.Features.Progress;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.API.Features.Translators;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.DiscoveryFactories;

/// <summary>
/// Builds the API's service document: every entry point a client may need, with no role checks of its
/// own. <see cref="ILinkFactory"/> removes the links whose target endpoint would answer 401 or 403 for
/// the current caller, so an anonymous caller gets the anonymous set, a translator theirs and an admin
/// theirs, all from one list and kept correct by the endpoints' own policies.
/// </summary>
/// <remarks>
/// Only entry points that take no id belong here. Actions keyed by an id, such as approve, delete and
/// import, are emitted on the resource that carries that id, which is where a client learns the id in
/// the first place.
/// </remarks>
internal sealed class DiscoveryLinkFactory : IDiscoveryLinkFactory
{
    private readonly ILinkFactory _linkFactory;

    public DiscoveryLinkFactory(ILinkFactory linkFactory)
    {
        _linkFactory = linkFactory;
    }

    public async ValueTask<List<LinkDto>> CreateDiscoveryLinksAsync()
    {
        List<LinkDto> links = [];

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(Features.Discovery.Discovery),
            rel: Rels.Self,
            method: HttpMethods.Get));

        // The file the CLI and the Avalonia app download without logging in. The route takes a
        // language, and Polish is the only one the platform serves.
        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(GetTranslationFile),
            rel: Rels.TranslationFile,
            method: HttpMethods.Get,
            values: new { lang = SupportedLanguages.Polish }));

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(GetPublicProgress),
            rel: Rels.Progress,
            method: HttpMethods.Get));

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(ListTranslations),
            rel: Rels.Translations,
            method: HttpMethods.Get));

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(UpsertTranslation),
            rel: Rels.Upsert,
            method: HttpMethods.Put));

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(GetTranslationStats),
            rel: Rels.TranslationStats,
            method: HttpMethods.Get));

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(BulkApproveTranslations),
            rel: Rels.BulkApprove,
            method: HttpMethods.Post));

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(ListGameVersions),
            rel: Rels.GameVersions,
            method: HttpMethods.Get));

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(RegisterGameVersion),
            rel: Rels.Register,
            method: HttpMethods.Post));

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(ExportMyContributionData),
            rel: Rels.ContributionDataExport,
            method: HttpMethods.Get));

        return links;
    }
}
