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

    /// <summary>
    /// The address a revert puts the account back on: the one it had before the first change since
    /// the last revert. It is set once per chain and never overwritten, so a second change cannot make
    /// itself an undo target, and it is what a revert restores — never the address the presented link
    /// happens to name (ADR-0048).
    /// </summary>
    public string? EmailChangeRevertTo { get; set; }
}
