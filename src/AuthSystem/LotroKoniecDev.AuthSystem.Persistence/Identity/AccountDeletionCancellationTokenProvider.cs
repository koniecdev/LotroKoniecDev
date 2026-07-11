using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.Persistence.Identity;

/// <summary>
/// Options for the cancel-deletion token provider. The default token providers cap
/// their lifespan at 24h (<see cref="DataProtectionTokenProviderOptions"/>), while the
/// cancellation link must stay valid for the whole deletion grace period — the API layer
/// rebinds <see cref="DataProtectionTokenProviderOptions.TokenLifespan"/> to
/// <c>Gdpr:DeletionGracePeriod</c> at startup.
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
/// Issues the one-time, signed, time-bounded token embedded in the cancel-deletion
/// email link. The token binds to the user's security stamp, so rotating the stamp
/// (which both scheduling and cancelling do) makes every previously issued token unusable.
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
