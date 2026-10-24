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
/// No role checks of its own: <see cref="ILinkFactory"/> drops every link whose target would answer 401
/// or 403 for this caller (ADR-0040). Only entry points that take no id belong here; id-keyed actions
/// live on the resource that carries the id.
/// </summary>
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
