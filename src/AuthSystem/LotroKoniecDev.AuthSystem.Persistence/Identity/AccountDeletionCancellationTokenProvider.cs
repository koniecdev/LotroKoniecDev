using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.Persistence.Identity;

/// <summary>
/// Options for the cancel-deletion token provider. The built-in providers keep a token valid for 24
/// hours (<see cref="DataProtectionTokenProviderOptions"/>), but the cancel link has to work for the
/// whole grace period. So the API sets
/// <see cref="DataProtectionTokenProviderOptions.TokenLifespan"/> to <c>Gdpr:DeletionGracePeriod</c>
/// at startup.
/// </summary>
public sealed class AccountDeletionCancellationTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public AccountDeletionCancellationTokenProviderOptions()
    {
        Name = AccountDeletionCancellationTokenProvider.ProviderName;
        TokenLifespan = TimeSpan.FromDays(14);
    }
}

/// <summary>
/// Creates the signed, single-use token with a time limit that goes into the cancel-deletion e-mail
/// link. The token is tied to the user's security stamp, and both scheduling and cancelling a deletion
/// change that stamp, so every token issued earlier stops working.
/// </summary>
public sealed class AccountDeletionCancellationTokenProvider : DataProtectorTokenProvider<ApplicationUser>
{
    public const string ProviderName = "AccountDeletionCancellation";
    public const string CancelDeletionPurpose = "CancelAccountDeletion";

    public AccountDeletionCancellationTokenProvider(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<AccountDeletionCancellationTokenProviderOptions> options,
        ILogger<AccountDeletionCancellationTokenProvider> logger)
        : base(dataProtectionProvider, options, logger)
    {
    }
}
