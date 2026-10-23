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

        // Allowed statuses listed, like EnsureCanBeDeleted, so the link never offers more than the
        // domain allows (#624).
        if (callerIsAdmin && status is GameVersionStatus.Unprocessed or GameVersionStatus.Superseded)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(DeleteGameVersion),
                rel: Rels.Delete,
                method: HttpMethods.Delete,
                values: new { id = id.Value }));
        }

        // On the item, not the service document, because an import needs the version id (#608).
        // MarkAsProcessed refuses a superseded version.
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
