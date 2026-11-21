using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Hateoas.AccountAdministrationFactories;

/// <summary>
/// The links follow the target account's state; whether the caller is the admin is left to the replayed
/// endpoint policy (ADR-0040).
/// </summary>
internal interface IAccountAdministrationLinkFactory
{
    ValueTask<List<LinkDto>> CreateAccountLinksAsync(Guid accountId, IReadOnlyList<string> roles, bool isDeletionScheduled);

    ValueTask<List<LinkDto>> CreateListLinksAsync(int page, bool hasNextPage);
}
