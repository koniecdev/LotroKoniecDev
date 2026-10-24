using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.Persistence.Identity;

/// <summary>
/// The cancel link has to work for the whole grace period, so the API sets
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
/// The token in the cancel-deletion e-mail link. It is bound to the security stamp, and both scheduling
/// and cancelling a deletion rotate the stamp, so every earlier token stops working.
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
