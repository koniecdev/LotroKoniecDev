using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Hateoas.AccountAggregateFactories;

/// <summary>
/// The links depend on the account's state, never on its role (ADR-0040).
/// </summary>
internal interface IAccountAggregateLinkFactory
{
    /// <param name="isEmailConfirmed">
    /// Decides whether <c>resend-email-confirmation</c> appears.
    /// </param>
    /// <param name="isDeletionScheduled">
    /// While a deletion is scheduled, only <c>self</c>, <c>cancel-deletion</c> (ADR-0031) and
    /// <c>download-account-data</c> (ADR-0052) are offered.
    /// </param>
    ValueTask<List<LinkDto>> CreateAccountLinksAsync(bool isEmailConfirmed, bool isDeletionScheduled);
}
