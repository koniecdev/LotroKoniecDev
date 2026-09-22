using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.RateLimiting;

/// <summary>
/// The budget that belongs to the account, not to the caller. The IP policies cannot stop an attacker who
/// rotates addresses, and behind the frontend they see one address for everybody; this is what makes a
/// brake per account (#692 for password-reset mail, #813 for password confirmations).
/// </summary>
public sealed class PerAccountFixedWindowThrottleTests
{
    /// <summary>The shipped confirmation budget: 10 per 15 minutes per account.</summary>
    private const int PermitLimit = AccountBudgets.PasswordConfirmationPermitLimit;

    [Fact]
    public void TryAcquire_ShouldAllowTheBudgetAndRefuseWhatFollows()
    {
        // Arrange
        using PerAccountFixedWindowThrottle throttle = new(PermitLimit, AccountBudgets.Window);
        Guid userId = Guid.CreateVersion7();

        // Act
        bool[] results = Enumerable.Range(0, PermitLimit + 2)
            .Select(_ => throttle.TryAcquire(userId))
            .ToArray();

        // Assert: the caller's address never enters the key, so this holds however many addresses they use
        results.Take(PermitLimit).ShouldAllBe(acquired => acquired);
        results.Skip(PermitLimit).ShouldAllBe(acquired => !acquired);
    }

    [Fact]
    public void TryAcquire_ShouldKeepOneAccountBudgetOutOfAnother()
    {
        // Arrange
        using PerAccountFixedWindowThrottle throttle = new(PermitLimit, AccountBudgets.Window);
        Guid attackedUserId = Guid.CreateVersion7();

        // Act: spend one account's budget in full
        for (int i = 0; i < PermitLimit; i++)
        {
            throttle.TryAcquire(attackedUserId);
        }

        // Assert: a flood at one account must not lock everybody else out
        throttle.TryAcquire(Guid.CreateVersion7()).ShouldBeTrue();
    }

    [Fact]
    public async Task TryAcquire_ShouldGiveTheBudgetBackWhenTheWindowPasses()
    {
        // Arrange: a wrong unit here — hours instead of minutes — would cut every user off for good, and
        // no test of the limit alone would notice
        TimeSpan window = TimeSpan.FromMilliseconds(200);
        using PerAccountFixedWindowThrottle throttle = new(permitLimit: 1, window);
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
        using PerAccountFixedWindowThrottle throttle = new(PermitLimit, AccountBudgets.Window);

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
            new PerAccountFixedWindowThrottle(permitLimit, AccountBudgets.Window).Dispose());
    }

    [Fact]
    public void Constructor_ShouldRejectAWindowThatNeverEnds()
    {
        // Act / Assert: a zero window would make the budget meaningless in one direction or the other
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PerAccountFixedWindowThrottle(permitLimit: 3, TimeSpan.Zero).Dispose());
    }
}
