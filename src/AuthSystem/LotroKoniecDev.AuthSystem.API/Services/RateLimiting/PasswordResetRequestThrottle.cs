using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// The password-reset budget: per account for a confirmed account, per inbox for an unconfirmed one
/// (ADR-0057).
/// </summary>
/// <remarks>
/// Only the inbox owner can confirm an account there, so a confirmed account's budget can only be spent
/// by mails that give her a working link. A budget per inbox would let a stranger's unconfirmed
/// <c>anna+x@</c> account use it up (ADR-0057 §3).
/// </remarks>
internal sealed class PasswordResetRequestThrottle : IPasswordResetRequestThrottle, IDisposable
{
    private readonly PerAccountFixedWindowThrottle _confirmedAccounts;
    private readonly PerMailboxFixedWindowThrottle _unconfirmedMailboxes;

    public PasswordResetRequestThrottle(int permitLimit, TimeSpan window)
    {
        _confirmedAccounts = new PerAccountFixedWindowThrottle(permitLimit, window);
        _unconfirmedMailboxes = new PerMailboxFixedWindowThrottle(permitLimit, window);
    }

    public bool TryAcquire(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.EmailConfirmed
            ? _confirmedAccounts.TryAcquire(user.Id)
            : _unconfirmedMailboxes.TryAcquire(MailboxKey.FromNormalizedEmail(user.NormalizedEmail));
    }

    public void Dispose()
    {
        _confirmedAccounts.Dispose();
        _unconfirmedMailboxes.Dispose();
    }
}
