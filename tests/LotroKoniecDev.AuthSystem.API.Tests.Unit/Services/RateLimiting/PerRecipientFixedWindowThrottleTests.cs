using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.RateLimiting;

/// <summary>
/// The budget that belongs to the address a mail goes to, for the one flow with no account behind that
/// address: the new address of an e-mail change (#793, ADR-0055). Whoever asks, and from whatever IP, the
/// inbox gets no more than the budget.
/// </summary>
public sealed class PerRecipientFixedWindowThrottleTests
{
    /// <summary>The shipped e-mail change budget: 3 per 15 minutes per new address.</summary>
    private const int PermitLimit = AccountBudgets.EmailChangeRecipientPermitLimit;

    private const string NormalizedEmail = "VICTIM@EXAMPLE.COM";

    [Fact]
    public void TryAcquire_ShouldAllowTheBudgetAndRefuseWhatFollows()
    {
        // Arrange
        using PerRecipientFixedWindowThrottle throttle = new(PermitLimit, AccountBudgets.Window);

        // Act
        bool[] results = Enumerable.Range(0, PermitLimit + 2)
            .Select(_ => throttle.TryAcquire(NormalizedEmail))
            .ToArray();

        // Assert
        results.Take(PermitLimit).ShouldAllBe(acquired => acquired);
        results.Skip(PermitLimit).ShouldAllBe(acquired => !acquired);
    }

    [Fact]
    public void TryAcquire_ShouldKeepOneAddressBudgetOutOfAnother()
    {
        // Arrange
        using PerRecipientFixedWindowThrottle throttle = new(PermitLimit, AccountBudgets.Window);

        // Act: spend one address's budget in full
        for (int i = 0; i < PermitLimit; i++)
        {
            throttle.TryAcquire(NormalizedEmail);
        }

        // Assert: a flood at one inbox must not block changes to every other address
        throttle.TryAcquire("SOMEONE-ELSE@EXAMPLE.COM").ShouldBeTrue();
    }

    [Fact]
    public async Task TryAcquire_ShouldGiveTheBudgetBackWhenTheWindowPasses()
    {
        // Arrange
        TimeSpan window = TimeSpan.FromMilliseconds(200);
        using PerRecipientFixedWindowThrottle throttle = new(permitLimit: 1, window);

        throttle.TryAcquire(NormalizedEmail).ShouldBeTrue();
        throttle.TryAcquire(NormalizedEmail).ShouldBeFalse();

        // Act: poll rather than sleep a fixed amount, because the limiter replenishes on its own timer
        bool replenished = false;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !replenished)
        {
            await Task.Delay(25);
            replenished = throttle.TryAcquire(NormalizedEmail);
        }

        // Assert
        replenished.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryAcquire_ShouldRejectAKeyThatWouldShareOneBudgetWithEveryCaller(string normalizedEmail)
    {
        // Arrange: the handler validates the address first, so an empty key is a programmer error
        using PerRecipientFixedWindowThrottle throttle = new(PermitLimit, AccountBudgets.Window);

        // Act / Assert
        Should.Throw<ArgumentException>(() => throttle.TryAcquire(normalizedEmail));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldRejectABudgetThatCouldNeverAllowAnything(int permitLimit)
    {
        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PerRecipientFixedWindowThrottle(permitLimit, AccountBudgets.Window).Dispose());
    }

    [Fact]
    public void Constructor_ShouldRejectAWindowThatNeverEnds()
    {
        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PerRecipientFixedWindowThrottle(permitLimit: 3, TimeSpan.Zero).Dispose());
    }
}
