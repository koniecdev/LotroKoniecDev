using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;

public sealed record AccountDataExportResponse(
    AuthDataExportDto AuthData,
    bool IsComplete) : ILinksResponse
{
    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}

public sealed record AuthDataExportDto(
    Guid UserId,
    string Username,
    string Email,
    string? PhoneNumber,
    bool EmailConfirmed,
    IReadOnlyList<string> Roles,
    bool DataProcessingConsentGiven,
    DateTimeOffset? DataProcessingConsentDate,
    bool PrivacyPolicyAccepted,
    DateTimeOffset? PrivacyPolicyAcceptedDate,
    bool TermsOfServiceAccepted,
    DateTimeOffset? TermsOfServiceAcceptedDate,
    DateTimeOffset? DeletionScheduledAt);
