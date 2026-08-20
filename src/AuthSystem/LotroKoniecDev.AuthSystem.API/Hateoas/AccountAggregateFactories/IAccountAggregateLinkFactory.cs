using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Hateoas.AccountAggregateFactories;

/// <summary>
/// Builds the HATEOAS links for the logged-in user's account. Which links appear depends on the state
/// of the account: <c>resend-email-confirmation</c>, for example, is only there while the address is
/// unconfirmed.
/// </summary>
internal interface IAccountAggregateLinkFactory
{
    /// <summary>
    /// Returns the full link set for the GDPR data export response.
    /// </summary>
    /// <param name="isEmailConfirmed">
    /// Whether the address is already confirmed. It decides whether
    /// <c>resend-email-confirmation</c> appears.
    /// </param>
    /// <param name="isDeletionScheduled">
    /// Whether a GDPR deletion is scheduled. While it is, the only link offered is
    /// <c>cancel-deletion</c> (ADR-0031).
    /// </param>
    ValueTask<List<LinkDto>> CreateAccountLinksAsync(bool isEmailConfirmed, bool isDeletionScheduled);
}
