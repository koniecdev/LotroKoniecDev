using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.RateLimiting;

/// <summary>
/// The budget of current-password confirmations that belongs to the account, not to the caller's
/// address (#813, ADR-0053). Every call to the account endpoints arrives from the frontend, so an IP key
/// is one bucket for all users; this is what makes the brake per account.
/// </summary>
public sealed class PasswordConfirmationThrottleTests
{
    /// <summary>Mirrors the shipped budget: 10 confirmations per 15 minutes per account.</summary>
    private const int PermitLimit = PasswordConfirmationThrottle.DefaultPermitLimit;

    [Fact]
    public void TryAcquire_ShouldAllowTheBudgetAndRefuseWhatFollows()
    {
        // Arrange
        using PasswordConfirmationThrottle throttle = new();
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
        using PasswordConfirmationThrottle throttle = new();
        Guid attackedUserId = Guid.CreateVersion7();

        // Act: spend one account's budget in full
        for (int i = 0; i < PermitLimit; i++)
        {
            throttle.TryAcquire(attackedUserId);
        }

        // Assert: guessing at one account must not lock everybody else out of their own actions
        throttle.TryAcquire(Guid.CreateVersion7()).ShouldBeTrue();
    }

    [Fact]
    public void TryAcquire_ShouldSpendAPermitOnEveryCall_SoTheGateIsAtomic()
    {
        // Arrange: the permit is taken before the password is checked, so it cannot depend on the
        // outcome. A budget that counted only failures would let a burst of guesses through the gate
        // before the first failure was recorded.
        using PasswordConfirmationThrottle throttle = new(permitLimit: 2, TimeSpan.FromMinutes(15));
        Guid userId = Guid.CreateVersion7();

        // Act
        bool first = throttle.TryAcquire(userId);
        bool second = throttle.TryAcquire(userId);
        bool third = throttle.TryAcquire(userId);

        // Assert
        first.ShouldBeTrue();
        second.ShouldBeTrue();
        third.ShouldBeFalse();
    }

    [Fact]
    public async Task TryAcquire_ShouldGiveTheBudgetBackWhenTheWindowPasses()
    {
        // Arrange: a wrong unit here — hours instead of minutes — would cut every user off from their
        // own account actions for good, and no test of the limit alone would notice
        TimeSpan window = TimeSpan.FromMilliseconds(200);
        using PasswordConfirmationThrottle throttle = new(permitLimit: 1, window);
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
        using PasswordConfirmationThrottle throttle = new();

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
            new PasswordConfirmationThrottle(permitLimit, TimeSpan.FromMinutes(15)).Dispose());
    }

    [Fact]
    public void Constructor_ShouldRejectAWindowThatNeverEnds()
    {
        // Act / Assert: a zero window would make the budget meaningless in one direction or the other
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PasswordConfirmationThrottle(permitLimit: 3, TimeSpan.Zero).Dispose());
    }
}
