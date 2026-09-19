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

    /// <summary>
    /// The same address as <see cref="EmailChangeRevertTo"/>, upper-cased by Identity's key
    /// normalizer. It is the indexed column the reservation of #684 looks the address up by, and it
    /// exists so the display form above can stay exactly as the user typed it: an account restored
    /// onto FOO@EXAMPLE.COM would be a visible defect in a recovery flow. Same pair, same reason, as
    /// Identity's own Email and NormalizedEmail.
    /// </summary>
    public string? NormalizedEmailChangeRevertTo { get; set; }

    /// <summary>
    /// When the undo above was armed. The address stays reserved against registration and against
    /// another account's e-mail change for exactly as long as the revert token lives, so nobody can
    /// take the address the owner still has a link back to (#684). A row armed before that ticket
    /// shipped has no timestamp and is not reserved.
    /// </summary>
    public DateTimeOffset? EmailChangeRevertArmedAt { get; set; }
}
