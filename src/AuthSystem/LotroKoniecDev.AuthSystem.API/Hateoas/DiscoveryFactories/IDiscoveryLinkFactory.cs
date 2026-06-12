using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Hateoas.DiscoveryFactories;

internal interface IDiscoveryLinkFactory
{
    List<LinkDto> CreateDiscoveryLinks(bool isAuthenticated);
}
