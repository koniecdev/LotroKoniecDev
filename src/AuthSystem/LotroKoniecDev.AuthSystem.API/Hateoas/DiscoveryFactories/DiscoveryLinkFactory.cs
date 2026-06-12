using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Features.Discovery;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.LinkFactories;

namespace LotroKoniecDev.AuthSystem.API.Hateoas.DiscoveryFactories;

internal sealed class DiscoveryLinkFactory : IDiscoveryLinkFactory
{
    private readonly ILinkFactory _linkFactory;

    public DiscoveryLinkFactory(ILinkFactory linkFactory)
    {
        _linkFactory = linkFactory;
    }

    public List<LinkDto> CreateDiscoveryLinks(bool isAuthenticated)
    {
        List<LinkDto> links = [];

        links.AddIfPresent(_linkFactory.Create(
            endpoint: nameof(Discovery),
            rel: Rels.Self,
            method: HttpMethods.Get));

        if (isAuthenticated)
        {
            links.AddIfPresent(_linkFactory.Create(
                endpoint: nameof(ExportAccountData),
                rel: Rels.ExportAccountData,
                method: HttpMethods.Get));
        }
        else
        {
            links.AddIfPresent(_linkFactory.Create(
                endpoint: nameof(RegisterUser),
                rel: Rels.Register,
                method: HttpMethods.Post));

            links.AddIfPresent(_linkFactory.Create(
                endpoint: nameof(ForgotPassword),
                rel: Rels.ForgotPassword,
                method: HttpMethods.Post));
        }

        return links;
    }
}
