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

        // Deleting a version registered by mistake is an admin action. A processed version is kept,
        // because translations point at it (spec 0001). A superseded version was registered and then
        // skipped, so deleting it is how the admin frees its version number again (#624).
        // It is written as a list of allowed statuses, like EnsureCanBeDeleted, so the link can never
        // offer more than the domain allows.
        if (callerIsAdmin && status is GameVersionStatus.Unprocessed or GameVersionStatus.Superseded)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(DeleteGameVersion),
                rel: Rels.Delete,
                method: HttpMethods.Delete,
                values: new { id = id.Value }));
        }

        // An import is always tied to the version it goes into, so the link lives on the item that
        // carries the id and not on the service document (#608). MarkAsProcessed refuses a superseded
        // version, so importing into one would lead nowhere.
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

        // Registering a version by hand is the admin's fallback for when reading the forum stops
        // working.
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
