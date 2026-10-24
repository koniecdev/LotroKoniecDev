using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.LinkFactories;

namespace LotroKoniecDev.AuthSystem.API.Hateoas.AccountAggregateFactories;

internal sealed class AccountAggregateLinkFactory : IAccountAggregateLinkFactory
{
    private readonly ILinkFactory _linkFactory;

    public AccountAggregateLinkFactory(ILinkFactory linkFactory)
    {
        _linkFactory = linkFactory;
    }

    public async ValueTask<List<LinkDto>> CreateAccountLinksAsync(bool isEmailConfirmed, bool isDeletionScheduled)
    {
        List<LinkDto> links = [];

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(ExportAccountData),
            rel: Rels.Self,
            method: HttpMethods.Get));

        // Taking a copy of your own data is a right, so the export is offered in both branches (ADR-0052).
        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(DownloadAccountData),
            rel: Rels.DownloadAccountData,
            method: HttpMethods.Post));

        // A locked account can only cancel the deletion; the other actions would lead nowhere.
        if (isDeletionScheduled)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(CancelAccountDeletion),
                rel: Rels.CancelDeletion,
                method: HttpMethods.Post));

            return links;
        }

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(ChangePassword),
            rel: Rels.ChangePassword,
            method: HttpMethods.Post));

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(RequestEmailChange),
            rel: Rels.ChangeEmail,
            method: HttpMethods.Post));

        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(DeleteAccount),
            rel: Rels.DeleteAccount,
            method: HttpMethods.Post));

        if (!isEmailConfirmed)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(ResendEmailConfirmation),
                rel: Rels.ResendEmailConfirmation,
                method: HttpMethods.Post));
        }

        return links;
    }
}
