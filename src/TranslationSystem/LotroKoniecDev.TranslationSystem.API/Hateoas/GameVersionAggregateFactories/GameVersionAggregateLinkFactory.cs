using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.LinkFactories;
using LotroKoniecDev.TranslationSystem.API.Features.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.GameVersionAggregateFactories;

internal sealed class GameVersionAggregateLinkFactory : IGameVersionAggregateLinkFactory
{
    private readonly ILinkFactory _linkFactory;

    public GameVersionAggregateLinkFactory(ILinkFactory linkFactory)
    {
        _linkFactory = linkFactory;
    }

    public List<LinkDto> CreateGameVersionLinks(GameVersionId id)
    {
        List<LinkDto> links = [];

        links.AddIfPresent(_linkFactory.Create(
            endpoint: nameof(GetGameVersion),
            rel: Rels.Self,
            method: HttpMethods.Get,
            values: new { id = id.Value }));

        return links;
    }

    public List<LinkDto> CreateCollectionLinks(bool callerIsAdmin)
    {
        List<LinkDto> links = [];

        links.AddIfPresent(_linkFactory.Create(
            endpoint: nameof(ListGameVersions),
            rel: Rels.Self,
            method: HttpMethods.Get));

        // Manually registering a version is the reviewer/admin fallback when the forum scrape breaks.
        if (callerIsAdmin)
        {
            links.AddIfPresent(_linkFactory.Create(
                endpoint: nameof(RegisterGameVersion),
                rel: Rels.Register,
                method: HttpMethods.Post));
        }

        return links;
    }
}
