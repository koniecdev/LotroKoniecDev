using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.LinkFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.DiscoveryFactories;

internal sealed class DiscoveryLinkFactory : IDiscoveryLinkFactory
{
    private readonly ILinkFactory _linkFactory;

    public DiscoveryLinkFactory(ILinkFactory linkFactory)
    {
        _linkFactory = linkFactory;
    }

    public List<LinkDto> CreateDiscoveryLinks()
    {
        List<LinkDto> links = [];

        links.AddIfPresent(_linkFactory.Create(
            endpoint: nameof(Features.Discovery.Discovery),
            rel: Rels.Self,
            method: HttpMethods.Get));

        return links;
    }
}
