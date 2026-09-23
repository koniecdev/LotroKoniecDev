using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// The password-reset budget: per account for a confirmed account, per inbox for an unconfirmed one
/// (ADR-0057).
/// </summary>
/// <remarks>
/// A confirmed account keeps a budget of its own on purpose. Only the inbox owner can confirm an account
/// at that inbox, because the link goes there. So the only way to spend this budget is to ask for resets
/// of the owner's own account, and each of those mails gives the owner a working link. A budget per inbox
/// would break that. A stranger could register <c>anna+x@gmail.com</c>, ask for resets on that account,
/// and use up Anna's budget with mails that cannot reset her own password.
/// Every account a stranger creates at someone else's inbox stays unconfirmed. Those accounts share one
/// budget per inbox, so each extra spelling does not bring a fresh budget.
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
