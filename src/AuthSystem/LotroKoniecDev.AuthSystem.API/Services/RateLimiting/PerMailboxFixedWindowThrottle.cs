using System.Threading.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// Each budget is its own instance, so a handler cannot spend the wrong one (ADR-0053 §1). Keyed on a
/// <see cref="MailboxKey"/>, not a string, so no spelling gets a budget of its own (ADR-0057).
/// </summary>
internal sealed class PerMailboxFixedWindowThrottle
    : IEmailConfirmationResendThrottle, IEmailChangeRecipientThrottle, IRegistrationMailboxThrottle, IDisposable
{
    private readonly PartitionedRateLimiter<MailboxKey> _limiter;

    public PerMailboxFixedWindowThrottle(int permitLimit, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        _limiter = PartitionedRateLimiter.Create<MailboxKey, MailboxKey>(mailbox =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: mailbox,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window
                }));
    }

    public bool TryAcquire(MailboxKey mailbox)
    {
        ArgumentNullException.ThrowIfNull(mailbox);

        // A fixed window keeps the permit when the lease is disposed.
        using RateLimitLease lease = _limiter.AttemptAcquire(mailbox);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        _limiter.Dispose();
    }
}
