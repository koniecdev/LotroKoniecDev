using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.LinkFactories;
using LotroKoniecDev.TranslationSystem.API.Features.GameVersions;
using LotroKoniecDev.TranslationSystem.API.Features.Import;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.GameVersionAggregateFactories;

internal sealed class GameVersionAggregateLinkFactory : IGameVersionAggregateLinkFactory
{
    private readonly ILinkFactory _linkFactory;

    public GameVersionAggregateLinkFactory(ILinkFactory linkFactory)
    {
        _linkFactory = linkFactory;
    }

    public async ValueTask<List<LinkDto>> CreateGameVersionLinksAsync(
        GameVersionId id,
        GameVersionStatus status,
        bool callerIsAdmin)
    {
        List<LinkDto> links = [];

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(GetGameVersion),
            rel: Rels.Self,
            method: HttpMethods.Get,
            values: new { id = id.Value }));

        // Deleting a mistaken manual registration is an admin action, and only an unprocessed version
        // may be removed (a processed/superseded one is referenced by translations — spec 0001).
        if (callerIsAdmin && status is GameVersionStatus.Unprocessed)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(DeleteGameVersion),
                rel: Rels.Delete,
                method: HttpMethods.Delete,
                values: new { id = id.Value }));
        }

        // Importing an exported.txt is keyed by the version it lands against, so the affordance lives
        // on the item that carries the id, not on the service document (#608). A superseded version is
        // the one state MarkAsProcessed refuses, so importing into it is a dead transition.
        if (callerIsAdmin && status is not GameVersionStatus.Superseded)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(ImportExportedTexts),
                rel: Rels.Import,
                method: HttpMethods.Post,
                values: new { id = id.Value }));
        }

        return links;
    }

    public async ValueTask<List<LinkDto>> CreateCollectionLinksAsync(bool callerIsAdmin)
    {
        List<LinkDto> links = [];

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(ListGameVersions),
            rel: Rels.Self,
            method: HttpMethods.Get));

        // Manually registering a version is the reviewer/admin fallback when the forum scrape breaks.
        if (callerIsAdmin)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(RegisterGameVersion),
                rel: Rels.Register,
                method: HttpMethods.Post));
        }

        return links;
    }
}
