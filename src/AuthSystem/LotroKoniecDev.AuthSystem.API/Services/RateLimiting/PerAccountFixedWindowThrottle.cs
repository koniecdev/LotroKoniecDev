using System.Threading.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// A fixed-window budget keyed on the account id: the one shape behind every per-account brake in this
/// app. Each brake is its own instance with its own budget (<see cref="AccountBudgets"/>), registered
/// under its own interface, so a handler can never spend the wrong one.
/// </summary>
/// <remarks>
/// The key is the account id, not the address the caller typed. Identity finds a user through
/// <c>NormalizeEmail</c>, which runs <c>Normalize()</c> before upper-casing, so "józef@wp.pl" written with
/// a combining accent resolves to the same account as the composed spelling. Keying on the text would
/// give those two identical-looking addresses a budget each, and Polish has eight letters that decompose.
/// An id cannot be spelled two ways.
/// The limit lives here and not in a limiter policy because a policy runs before the endpoint, where
/// nothing is known about the account yet. Reading the form body there to learn the address would block a
/// thread-pool thread until the whole upload arrived, so a slow upload flood would starve the pool
/// through the component meant to protect it.
/// The budget is in process, like every IP policy in this app, so two running containers mean two
/// budgets and a restart empties it. That is the existing trade-off, not a new one.
/// </remarks>
internal sealed class PerAccountFixedWindowThrottle : IPasswordResetRequestThrottle, IPasswordConfirmationThrottle, IDisposable
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
        // A fixed window never gives a permit back when the lease is disposed, so this takes exactly
        // one. The same code over a concurrency limiter would return it and count nothing.
        using RateLimitLease lease = _limiter.AttemptAcquire(userId);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        _limiter.Dispose();
    }
}
