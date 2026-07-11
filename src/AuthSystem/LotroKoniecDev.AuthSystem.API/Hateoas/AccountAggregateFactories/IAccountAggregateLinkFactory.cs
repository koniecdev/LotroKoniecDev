using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Hateoas.AccountAggregateFactories;

/// <summary>
/// Builds state-aware HATEOAS links for the authenticated user's account
/// resource. The shape of the link set varies with the account's observable
/// state — e.g. <c>resend-email-confirmation</c> only appears while the
/// user's email is unconfirmed.
/// </summary>
internal interface IAccountAggregateLinkFactory
{
    /// <summary>
    /// Returns the full link set for the GDPR data-export envelope.
    /// </summary>
    /// <param name="isEmailConfirmed">
    /// Whether the user's email has already been confirmed. Drives the
    /// visibility of the <c>resend-email-confirmation</c> state transition.
    /// </param>
    /// <param name="isDeletionScheduled">
    /// Whether GDPR deletion is scheduled for the account. While scheduled,
    /// the only advertised transition is <c>cancel-deletion</c> (ADR-0031).
    /// </param>
    List<LinkDto> CreateAccountLinks(bool isEmailConfirmed, bool isDeletionScheduled);
}
