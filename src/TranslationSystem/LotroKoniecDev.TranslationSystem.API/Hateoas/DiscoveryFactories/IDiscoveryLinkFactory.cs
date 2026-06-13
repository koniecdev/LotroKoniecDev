using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.DiscoveryFactories;

internal interface IDiscoveryLinkFactory
{
    List<LinkDto> CreateDiscoveryLinks();
}
