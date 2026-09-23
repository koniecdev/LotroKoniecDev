using System.Threading.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// A fixed-window budget keyed on the inbox a mail reaches: the shape behind every per-inbox send budget
/// in this app. Each budget is its own instance, so a handler can never spend the wrong one (ADR-0053 §1).
/// </summary>
/// <remarks>
/// The key is a <see cref="MailboxKey"/>, not a string, so every budget goes through the one fold that
/// type owns and no spelling gets a budget of its own (#835, ADR-0057).
/// </remarks>
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

        // A fixed window never gives a permit back when the lease is disposed, so this takes exactly one.
        using RateLimitLease lease = _limiter.AttemptAcquire(mailbox);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        _limiter.Dispose();
    }
}
