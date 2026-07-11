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
}
