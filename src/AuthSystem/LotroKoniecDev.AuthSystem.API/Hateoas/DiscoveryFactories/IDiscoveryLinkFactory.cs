using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Hateoas.DiscoveryFactories;

internal interface IDiscoveryLinkFactory
{
    ValueTask<List<LinkDto>> CreateDiscoveryLinksAsync(bool isAuthenticated);
}
