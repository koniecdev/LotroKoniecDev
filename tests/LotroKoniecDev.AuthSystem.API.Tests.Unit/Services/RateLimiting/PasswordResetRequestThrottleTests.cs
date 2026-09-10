using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.RateLimiting;

/// <summary>
/// The send budget that belongs to the account the mail would reach, not to the caller. The IP policies
/// cannot stop an attacker who rotates IPs from mailing one victim, which is what this closes (#692).
/// </summary>
public sealed class PasswordResetRequestThrottleTests
{
    /// <summary>Mirrors the shipped budget: 3 sends per 15 minutes per account.</summary>
    private const int PermitLimit = PasswordResetRequestThrottle.DefaultPermitLimit;

    [Fact]
    public void TryAcquire_ShouldAllowTheBudgetAndRefuseWhatFollows()
    {
        // Arrange
        using PasswordResetRequestThrottle throttle = new();
        Guid userId = Guid.CreateVersion7();

        // Act
        bool[] results = Enumerable.Range(0, PermitLimit + 2)
            .Select(_ => throttle.TryAcquire(userId))
            .ToArray();

        // Assert: the caller's IP never enters the key, so this holds however many IPs they use
        results.Take(PermitLimit).ShouldAllBe(acquired => acquired);
        results.Skip(PermitLimit).ShouldAllBe(acquired => !acquired);
    }

    [Fact]
    public void TryAcquire_ShouldKeepOneAccountBudgetOutOfAnother()
    {
        // Arrange
        using PasswordResetRequestThrottle throttle = new();
        Guid floodedUserId = Guid.CreateVersion7();

        // Act: spend one account's budget in full
        for (int i = 0; i < PermitLimit; i++)
        {
            throttle.TryAcquire(floodedUserId);
        }

        // Assert: a flood at one inbox must not lock everybody else out of password reset
        throttle.TryAcquire(Guid.CreateVersion7()).ShouldBeTrue();
    }

    [Fact]
    public async Task TryAcquire_ShouldGiveTheBudgetBackWhenTheWindowPasses()
    {
        // Arrange: a wrong unit here — hours instead of minutes — would cut every user off from password
        // reset for good, and no test of the limit alone would notice
        TimeSpan window = TimeSpan.FromMilliseconds(200);
        using PasswordResetRequestThrottle throttle = new(permitLimit: 1, window);
        Guid userId = Guid.CreateVersion7();

        throttle.TryAcquire(userId).ShouldBeTrue();
        throttle.TryAcquire(userId).ShouldBeFalse();

        // Act: poll rather than sleep a fixed amount — the limiter replenishes on its own timer, and a
        // loaded machine must not be the reason this fails
        bool replenished = false;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !replenished)
        {
            await Task.Delay(25);
            replenished = throttle.TryAcquire(userId);
        }

        // Assert
        replenished.ShouldBeTrue();
    }

    [Fact]
    public void TryAcquire_ShouldCountAnEmptyIdLikeAnyOther()
    {
        // Arrange: an id is never empty in production, but the budget must not silently become shared
        using PasswordResetRequestThrottle throttle = new();

        // Act
        bool[] results = Enumerable.Range(0, PermitLimit + 1)
            .Select(_ => throttle.TryAcquire(Guid.Empty))
            .ToArray();

        // Assert
        results.Take(PermitLimit).ShouldAllBe(acquired => acquired);
        results[PermitLimit].ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldRejectABudgetThatCouldNeverAllowAnything(int permitLimit)
    {
        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PasswordResetRequestThrottle(permitLimit, TimeSpan.FromMinutes(15)).Dispose());
    }

    [Fact]
    public void Constructor_ShouldRejectAWindowThatNeverEnds()
    {
        // Act / Assert: a zero window would make the budget meaningless in one direction or the other
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PasswordResetRequestThrottle(permitLimit: 3, TimeSpan.Zero).Dispose());
    }
}
