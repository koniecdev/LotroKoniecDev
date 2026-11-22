using LotroKoniecDev.AuthSystem.API.Features.Admin;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.LinkFactories;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Hateoas.AccountAdministrationFactories;

internal sealed class AccountAdministrationLinkFactory : IAccountAdministrationLinkFactory
{
    private readonly ILinkFactory _linkFactory;

    public async ValueTask<List<LinkDto>> CreateAccountLinksAsync(
        Guid accountId,
        IReadOnlyList<string> roles,
        bool isDeletionScheduled)
    {
        List<LinkDto> links = [];

        if (ReviewerRole.CanBeGranted(roles, isDeletionScheduled))
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(GrantReviewerRole),
                rel: Rels.GrantReviewer,
                method: HttpMethods.Put,
                values: new { accountId }));
        }

        // Taking the role away is offered whatever the account's state.
        if (roles.Contains(AuthConstants.Roles.Reviewer))
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(RemoveReviewerRole),
                rel: Rels.RemoveReviewer,
                method: HttpMethods.Delete,
                values: new { accountId }));
        }

        return links;
    }

    public async ValueTask<List<LinkDto>> CreateListLinksAsync(int page, bool hasNextPage)
    {
        List<LinkDto> links = [];

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(ListAccounts),
            rel: Rels.Self,
            method: HttpMethods.Get,
            values: new { page }));

        if (page > 1)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(ListAccounts),
                rel: Rels.PreviousPage,
                method: HttpMethods.Get,
                values: new { page = page - 1 }));
        }

        if (hasNextPage)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(ListAccounts),
                rel: Rels.NextPage,
                method: HttpMethods.Get,
                values: new { page = page + 1 }));
        }

        return links;
    }

    public AccountAdministrationLinkFactory(ILinkFactory linkFactory)
    {
        _linkFactory = linkFactory;
    }
}
