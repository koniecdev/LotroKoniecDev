using System.Threading.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// Each brake is its own instance (<see cref="AccountBudgets"/>), so a handler cannot spend the wrong one.
/// </summary>
/// <remarks>
/// Keyed on the account id, not the typed address: Identity normalizes Unicode, so "józef@wp.pl" with a
/// combining accent finds the same account, and keying on text would give it a second budget.
/// Not a limiter policy: a policy runs before the account is known, and reading the form body there
/// would let a slow upload flood starve the thread pool.
/// In process, like every IP policy here, so two containers mean two budgets and a restart empties it.
/// </remarks>
internal sealed class PerAccountFixedWindowThrottle
    : IPasswordConfirmationThrottle, IAccountDeletionScheduleThrottle, IDeletionScheduledLoginThrottle, IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter;

    public PerAccountFixedWindowThrottle(int permitLimit, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        _limiter = PartitionedRateLimiter.Create<Guid, Guid>(userId =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: userId,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window
                }));
    }

    public bool TryAcquire(Guid userId)
    {
        // A fixed window keeps the permit when the lease is disposed; a concurrency limiter would not.
        using RateLimitLease lease = _limiter.AttemptAcquire(userId);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        _limiter.Dispose();
    }
}
