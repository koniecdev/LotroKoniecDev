using LotroKoniecDev.AuthSystem.API.Features.Admin;
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

    public async ValueTask<List<LinkDto>> CreateDiscoveryLinksAsync(bool isAuthenticated)
    {
        List<LinkDto> links = [];

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(Discovery),
            rel: Rels.Self,
            method: HttpMethods.Get));

        if (isAuthenticated)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(ExportAccountData),
                rel: Rels.ExportAccountData,
                method: HttpMethods.Get));

            // Only the admin gets it: the link factory replays the endpoint's admin policy (ADR-0040).
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(ListAccounts),
                rel: Rels.Accounts,
                method: HttpMethods.Get));
        }
        else
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(RegisterUser),
                rel: Rels.Register,
                method: HttpMethods.Post));

            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(ForgotPassword),
                rel: Rels.ForgotPassword,
                method: HttpMethods.Post));
        }

        return links;
    }
}
