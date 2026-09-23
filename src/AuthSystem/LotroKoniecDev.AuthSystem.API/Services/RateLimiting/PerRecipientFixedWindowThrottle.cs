using System.Threading.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// A fixed-window budget keyed on a recipient address: the one per-address brake in this app that has no
/// account to key on. It is the twin of <see cref="PerAccountFixedWindowThrottle"/> for that case only.
/// </summary>
/// <remarks>
/// Every other in-handler budget keys on the account id, because an id cannot be spelled two ways (#692).
/// An e-mail change link goes to an address no account owns yet, so the key here is that address after
/// <c>UserManager.NormalizeEmail</c>: the same folding Identity uses to decide two spellings are one
/// account. The key is the caller's job, because the normalizer is scoped and this budget is a singleton
/// (ADR-0055).
/// </remarks>
internal sealed class PerRecipientFixedWindowThrottle : IEmailChangeRecipientThrottle, IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public PerRecipientFixedWindowThrottle(int permitLimit, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        _limiter = PartitionedRateLimiter.Create<string, string>(normalizedEmail =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: normalizedEmail,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window
                }));
    }

    public bool TryAcquire(string normalizedEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedEmail);

        // A fixed window never gives a permit back when the lease is disposed, so this takes exactly one.
        using RateLimitLease lease = _limiter.AttemptAcquire(normalizedEmail);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        _limiter.Dispose();
    }
}
