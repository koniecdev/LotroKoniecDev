using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Hateoas.DiscoveryFactories;

internal interface IDiscoveryLinkFactory
{
    ValueTask<List<LinkDto>> CreateDiscoveryLinksAsync();
}
