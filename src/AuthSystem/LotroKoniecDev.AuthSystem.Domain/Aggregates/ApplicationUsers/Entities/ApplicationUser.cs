using Microsoft.AspNetCore.Identity;

namespace LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>
    /// Registration no longer sets this: the account runs on the contract, not on consent (ADR-0068).
    /// It keeps what older accounts gave until the column is dropped (#1130).
    /// </summary>
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
    /// The address a revert restores: the one the account had before the first change since the last
    /// revert. It is set once per chain and never overwritten, so a second change cannot make itself
    /// the undo target. A revert never restores the address the presented link names (ADR-0048).
    /// </summary>
    public string? EmailChangeRevertTo { get; set; }

    /// <summary>
    /// The indexed lookup form of <see cref="EmailChangeRevertTo"/> for the reservation (#684), the same
    /// pair as Identity's Email and NormalizedEmail. The display form stays as the user typed it, so a
    /// restored account does not come back as FOO@EXAMPLE.COM.
    /// </summary>
    public string? NormalizedEmailChangeRevertTo { get; set; }

    /// <summary>
    /// While the revert token lives, the address stays reserved against registration, another account's
    /// e-mail change and the admin seed, so nobody can take an address the owner still has a link back
    /// to (#684). Null means the address is not reserved.
    /// </summary>
    public DateTimeOffset? EmailChangeRevertArmedAt { get; set; }

    /// <summary>
    /// Arms the undo at the address the account is leaving. The three fields only mean something
    /// together, so they are written in one place.
    /// </summary>
    /// <param name="previousEmail">The address to go back to, as the user typed it.</param>
    /// <param name="normalizedPreviousEmail">The same address through Identity's key normalizer.</param>
    /// <param name="armedAt">When the undo was armed, which starts the reservation window.</param>
    public void ArmEmailChangeRevert(
        string previousEmail, string normalizedPreviousEmail, DateTimeOffset armedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPreviousEmail);


        EmailChangeRevertTo = previousEmail;
        NormalizedEmailChangeRevertTo = normalizedPreviousEmail;
        EmailChangeRevertArmedAt = armedAt;
    }

    /// <summary>
    /// Ends the chain: nothing left to undo or reserve. When outstanding links must die too, the caller
    /// also rotates <see cref="EmailChangeRevertStamp"/>.
    /// </summary>
    public void DisarmEmailChangeRevert()
    {
        EmailChangeRevertTo = null;
        NormalizedEmailChangeRevertTo = null;
        EmailChangeRevertArmedAt = null;
    }
}
