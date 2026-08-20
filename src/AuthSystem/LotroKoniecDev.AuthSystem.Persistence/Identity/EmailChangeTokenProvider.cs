using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.Persistence.Identity;

/// <summary>
/// Options for the e-mail change token provider. The link lives 24 hours, the same as the activation
/// link users already know.
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
/// Creates the token behind the "confirm your new address" link. The purpose carries the target
/// address, so a token made for one address cannot confirm another, and the token is tied to the
/// user's security stamp, which makes it single-use: applying the change rotates the stamp.
/// </summary>
/// <remarks>
/// Identity ships <c>GenerateChangeEmailTokenAsync</c> and <c>ChangeEmailAsync</c> and both are
/// public, so this provider does not exist because that API is missing. It exists because
/// <c>ChangeEmailAsync</c> checks the token inside itself against a purpose string Identity keeps
/// private. A caller could then only enqueue its outbox row first and call afterwards, and a bad
/// token returns before the save, leaving that row tracked in a context something else may still
/// flush. Owning the purpose lets the handler check the token first and then commit the row and the
/// address change together (ADR-0048).
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
