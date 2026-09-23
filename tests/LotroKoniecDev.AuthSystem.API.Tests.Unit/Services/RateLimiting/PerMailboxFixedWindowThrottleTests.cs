using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.RateLimiting;

/// <summary>
/// The budget that belongs to the inbox a mail goes to (#793, #835, ADR-0055, ADR-0057). Whoever asks, and
/// from whatever IP, the inbox gets no more than the budget.
/// </summary>
public sealed class PerMailboxFixedWindowThrottleTests
{
    /// <summary>The shipped e-mail change budget: 3 per 15 minutes per inbox.</summary>
    private const int PermitLimit = AccountBudgets.EmailChangeRecipientPermitLimit;

    private static readonly MailboxKey Mailbox = MailboxKey.FromNormalizedEmail("VICTIM@EXAMPLE.COM");

    [Fact]
    public void TryAcquire_ShouldAllowTheBudgetAndRefuseWhatFollows()
    {
        // Arrange
        using PerMailboxFixedWindowThrottle throttle = new(PermitLimit, AccountBudgets.Window);

        // Act
        bool[] results = Enumerable.Range(0, PermitLimit + 2)
            .Select(_ => throttle.TryAcquire(Mailbox))
            .ToArray();

        // Assert
        results.Take(PermitLimit).ShouldAllBe(acquired => acquired);
        results.Skip(PermitLimit).ShouldAllBe(acquired => !acquired);
    }

    [Fact]
    public void TryAcquire_ShouldCountEverySpellingOfOneInboxAgainstOneBudget()
    {
        // Arrange
        using PerMailboxFixedWindowThrottle throttle = new(PermitLimit, AccountBudgets.Window);
        string[] spellings = ["ANNA+1@GMAIL.COM", "A.NNA@GMAIL.COM", "ANNA@GOOGLEMAIL.COM"];
        spellings.Length.ShouldBe(PermitLimit);

        foreach (string spelling in spellings)
        {
            throttle.TryAcquire(MailboxKey.FromNormalizedEmail(spelling)).ShouldBeTrue();
        }

        // Act
        bool acquired = throttle.TryAcquire(MailboxKey.FromNormalizedEmail("ANNA@GMAIL.COM"));

        // Assert
        acquired.ShouldBeFalse();
    }

    [Fact]
    public void TryAcquire_ShouldKeepOneInboxBudgetOutOfAnother()
    {
        // Arrange
        using PerMailboxFixedWindowThrottle throttle = new(PermitLimit, AccountBudgets.Window);

        // Act: spend one inbox's budget in full
        for (int i = 0; i < PermitLimit; i++)
        {
            throttle.TryAcquire(Mailbox);
        }

        // Assert: a flood at one inbox must not block mail to every other inbox
        throttle.TryAcquire(MailboxKey.FromNormalizedEmail("SOMEONE-ELSE@EXAMPLE.COM")).ShouldBeTrue();
    }

    [Fact]
    public async Task TryAcquire_ShouldGiveTheBudgetBackWhenTheWindowPasses()
    {
        // Arrange
        TimeSpan window = TimeSpan.FromMilliseconds(200);
        using PerMailboxFixedWindowThrottle throttle = new(permitLimit: 1, window);

        throttle.TryAcquire(Mailbox).ShouldBeTrue();
        throttle.TryAcquire(Mailbox).ShouldBeFalse();

        // Act: poll rather than sleep a fixed amount, because the limiter replenishes on its own timer
        bool replenished = false;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !replenished)
        {
            await Task.Delay(25);
            replenished = throttle.TryAcquire(Mailbox);
        }

        // Assert
        replenished.ShouldBeTrue();
    }

    [Fact]
    public void TryAcquire_ShouldRejectAMissingMailbox()
    {
        // Arrange
        using PerMailboxFixedWindowThrottle throttle = new(PermitLimit, AccountBudgets.Window);

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => throttle.TryAcquire(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldRejectABudgetThatCouldNeverAllowAnything(int permitLimit)
    {
        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PerMailboxFixedWindowThrottle(permitLimit, AccountBudgets.Window).Dispose());
    }

    [Fact]
    public void Constructor_ShouldRejectAWindowThatNeverEnds()
    {
        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PerMailboxFixedWindowThrottle(permitLimit: 3, TimeSpan.Zero).Dispose());
    }
}
