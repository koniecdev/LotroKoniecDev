using System.Threading.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

/// <summary>
/// The per-account brake on guessing the current password from inside a session (#813, ADR-0053).
/// </summary>
/// <remarks>
/// Every attempt spends a permit, a correct password too: the permit is taken before the check, so a
/// burst of concurrent guesses cannot pass the gate, and a fixed window cannot give one back afterwards.
/// The key is the account id from the token, never an address, for the same reason as
/// <see cref="PasswordResetRequestThrottle"/>. ADR-0053 holds the rest.
/// </remarks>
internal sealed class PasswordConfirmationThrottle : IPasswordConfirmationThrottle, IDisposable
{
    /// <summary>
    /// The same room the login form gives a client for wrong passwords (auth-page-limit): enough for a
    /// few typos and every sensitive action in a row, far too few for a guessing script.
    /// </summary>
    internal const int DefaultPermitLimit = 10;

    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(15);

    private readonly PartitionedRateLimiter<Guid> _limiter;

    public PasswordConfirmationThrottle() : this(DefaultPermitLimit, DefaultWindow)
    {
    }

    /// <summary>
    /// Takes the budget explicitly so a test can prove that it replenishes without waiting a quarter of
    /// an hour for it.
    /// </summary>
    internal PasswordConfirmationThrottle(int permitLimit, TimeSpan window)
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
        // A fixed window never gives a permit back when the lease is disposed, so this takes exactly one.
        using RateLimitLease lease = _limiter.AttemptAcquire(userId);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        _limiter.Dispose();
    }
}
