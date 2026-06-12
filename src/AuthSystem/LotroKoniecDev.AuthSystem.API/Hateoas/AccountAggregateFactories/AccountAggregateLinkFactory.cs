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

    public List<LinkDto> CreateAccountLinks(bool isEmailConfirmed)
    {
        List<LinkDto> links = [];

        // Self — GET the account data export (the resource itself)
        links.AddIfPresent(_linkFactory.Create(
            endpoint: nameof(ExportAccountData),
            rel: Rels.Self,
            method: HttpMethods.Get));

        // Always-available state transitions for an authenticated, active account
        links.AddIfPresent(_linkFactory.Create(
            endpoint: nameof(ChangePassword),
            rel: Rels.ChangePassword,
            method: HttpMethods.Post));

        links.AddIfPresent(_linkFactory.Create(
            endpoint: nameof(DeleteAccount),
            rel: Rels.DeleteAccount,
            method: HttpMethods.Post));

        // State-aware: resending an email confirmation only makes sense
        // while the email is still unconfirmed. Once confirmed, the link
        // disappears so clients do not advertise a dead transition.
        if (!isEmailConfirmed)
        {
            links.AddIfPresent(_linkFactory.Create(
                endpoint: nameof(ResendEmailConfirmation),
                rel: Rels.ResendEmailConfirmation,
                method: HttpMethods.Post));
        }

        return links;
    }
}
