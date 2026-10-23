using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.Persistence.Identity;

/// <summary>
/// 24 hours, the same as the activation link users already know.
/// </summary>
public sealed class EmailChangeTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public EmailChangeTokenProviderOptions()
    {
        Name = EmailChangeTokenProvider.ProviderName;
        TokenLifespan = TimeSpan.FromHours(24);
    }
}

/// <summary>
/// The token behind the "confirm your new address" link. The purpose carries the target address, so a
/// token for one address cannot confirm another. The token is bound to the security stamp, and applying
/// the change rotates it, so the token is single-use.
/// </summary>
/// <remarks>
/// Identity's <c>ChangeEmailAsync</c> checks the token inside itself against a private purpose string,
/// so a caller would have to enqueue its outbox row first, and a bad token would leave that row tracked.
/// Owning the purpose lets the handler check the token first, then commit the row and the address change
/// together (ADR-0048).
/// </remarks>
public sealed class EmailChangeTokenProvider : DataProtectorTokenProvider<ApplicationUser>
{
    public const string ProviderName = "EmailChange";

    public EmailChangeTokenProvider(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<EmailChangeTokenProviderOptions> options,
        ILogger<EmailChangeTokenProvider> logger)
        : base(dataProtectionProvider, options, logger)
    {
    }

    public static string PurposeFor(string newEmail) => $"ChangeEmail:{newEmail}";
}
