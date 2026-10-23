using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// One shape for two endpoints, so they cannot describe the same account differently (#690). The export
/// adds the contact details no page renders, so the password buys something the account page does not
/// already hand over (ADR-0052).
/// </summary>
internal static class AccountDataExportReader
{
    /// <summary>What the "Moje konto" page renders, on every visit, so no password.</summary>
    internal static Task<AuthDataExportDto> ReadRepresentationAsync(
        UserManager<ApplicationUser> userManager,
        ApplicationUser user) =>
        ReadAsync(userManager, user, includeContactDetails: false);

    /// <summary>The GDPR Art. 15 document, handed over only behind the current password.</summary>
    internal static Task<AuthDataExportDto> ReadExportAsync(
        UserManager<ApplicationUser> userManager,
        ApplicationUser user) =>
        ReadAsync(userManager, user, includeContactDetails: true);

    private static async Task<AuthDataExportDto> ReadAsync(
        UserManager<ApplicationUser> userManager,
        ApplicationUser user,
        bool includeContactDetails)
    {
        IList<string> roles = await userManager.GetRolesAsync(user);

        return new AuthDataExportDto(
            user.Id,
            user.UserName ?? string.Empty,
            user.Email ?? string.Empty,
            includeContactDetails ? user.PhoneNumber : null,
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
