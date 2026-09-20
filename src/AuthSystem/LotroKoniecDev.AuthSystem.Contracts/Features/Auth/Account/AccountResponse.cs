using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;

/// <summary>
/// The caller's own account, as the account pages show it. Its <c>Links</c> decide what the caller may
/// do next. It is not the GDPR export: that one is a separate document and asks for the password first
/// (#690).
/// </summary>
public sealed record AccountResponse(AccountDto Account) : ILinksResponse
{
    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}

public sealed record AccountDto(
    string Username,
    string Email,
    bool EmailConfirmed,
    IReadOnlyList<string> Roles,
    bool DataProcessingConsentGiven,
    DateTimeOffset? DataProcessingConsentDate,
    bool PrivacyPolicyAccepted,
    DateTimeOffset? PrivacyPolicyAcceptedDate,
    bool TermsOfServiceAccepted,
    DateTimeOffset? TermsOfServiceAcceptedDate,
    DateTimeOffset? DeletionScheduledAt);
