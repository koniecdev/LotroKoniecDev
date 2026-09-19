using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Builds the auth half of the GDPR Art. 15 document. Two endpoints need it: the GET that the account
/// page renders and the password-gated POST that hands the export over (#690). The shape lives here so
/// the two can never drift apart and start describing the same account differently.
/// </summary>
internal static class AccountDataExportMapper
{
    internal static async Task<AuthDataExportDto> ToDtoAsync(
        UserManager<ApplicationUser> userManager,
        ApplicationUser user)
    {
        IList<string> roles = await userManager.GetRolesAsync(user);

        return new AuthDataExportDto(
            user.Id,
            user.UserName ?? string.Empty,
            user.Email ?? string.Empty,
            user.PhoneNumber,
            user.EmailConfirmed,
            roles.ToList(),
            user.DataProcessingConsentGiven,
            user.DataProcessingConsentDate,
            user.PrivacyPolicyAccepted,
            user.PrivacyPolicyAcceptedDate,
            user.TermsOfServiceAccepted,
            user.TermsOfServiceAcceptedDate,
            user.DeletionScheduledAt);
    }
}
