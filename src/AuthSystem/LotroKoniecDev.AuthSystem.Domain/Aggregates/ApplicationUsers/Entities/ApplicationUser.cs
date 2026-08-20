using Microsoft.AspNetCore.Identity;

namespace LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public bool DataProcessingConsentGiven { get; set; }
    public DateTimeOffset? DataProcessingConsentDate { get; set; }
    public bool PrivacyPolicyAccepted { get; set; }
    public DateTimeOffset? PrivacyPolicyAcceptedDate { get; set; }
    public bool TermsOfServiceAccepted { get; set; }
    public DateTimeOffset? TermsOfServiceAcceptedDate { get; set; }
    public DateTimeOffset? DeletionScheduledAt { get; set; }

    /// <summary>
    /// Invalidates every revert link issued before the last successful revert (ADR-0048). It cannot be
    /// the security stamp: a password change rotates that, and surviving a password change is the one
    /// thing a revert token has to do.
    /// </summary>
    public Guid? EmailChangeRevertStamp { get; set; }
}
