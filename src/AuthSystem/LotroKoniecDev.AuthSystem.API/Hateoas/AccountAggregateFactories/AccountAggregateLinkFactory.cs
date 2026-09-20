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

        // self: a GET of the account data export, which is this resource.
        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(ExportAccountData),
            rel: Rels.Self,
            method: HttpMethods.Get));

        // Taking a copy of your own data is a right, so the export is offered in both branches and the
        // endpoint serves it in both (#690). ADR-0052 says how short that window really is.
        links.AddIfPresent(await _linkFactory.CreateAsync(
            endpoint: nameof(DownloadAccountData),
            rel: Rels.DownloadAccountData,
            method: HttpMethods.Post));

        // While a deletion is scheduled the account is locked, and the only thing left to do is cancel
        // it with the single-use token from the e-mail. The other account actions would lead nowhere,
        // so they are left out.
        if (isDeletionScheduled)
        {
            links.AddIfPresent(await _linkFactory.CreateAsync(
                endpoint: nameof(CancelAccountDeletion),
                rel: Rels.CancelDeletion,
                method: HttpMethods.Post));

            return links;
        }

        // Actions an active, logged-in account can always take.
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

        // Resending the confirmation e-mail only makes sense while the address is unconfirmed. Once it
        // is confirmed the link disappears, so no client offers an action that leads nowhere.
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
