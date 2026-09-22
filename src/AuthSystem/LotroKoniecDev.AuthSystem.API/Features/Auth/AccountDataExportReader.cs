using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Reads the auth half of the account data. Two endpoints need it and they are not allowed to describe
/// the same account differently, so the shape lives here (#690).
/// They do not serve the same fields. The representation carries what the account page shows; the
/// export adds the contact details, which no page renders, so the password buys the reader something
/// the account page does not already hand over. ADR-0052 holds the reasoning.
/// </summary>
internal static class AccountDataExportReader
{
    /// <summary>What the "Moje konto" page renders. No password, because the page loads it on every visit.</summary>
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
