using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.RateLimiting;

/// <summary>
/// The password-reset budget (#692, #835, ADR-0057): per account for a confirmed account, per inbox for
/// unconfirmed ones. The split is what stops a stranger from blocking a real user's password recovery with
/// an account registered at a spelling of her address.
/// </summary>
public sealed class PasswordResetRequestThrottleTests
{
    private const int PermitLimit = AccountBudgets.PasswordResetPermitLimit;

    [Fact]
    public void TryAcquire_ShouldKeepTheOwnersBudget_WhenUnconfirmedSpellingsOfHerInboxSpendTheirs()
    {
        // Arrange: Anna is confirmed; the stranger's account at a +tag spelling of her inbox is not
        using PasswordResetRequestThrottle throttle = new(PermitLimit, AccountBudgets.Window);
        ApplicationUser owner = User("ANNA@GMAIL.COM", emailConfirmed: true);
        ApplicationUser sibling = User("ANNA+X@GMAIL.COM", emailConfirmed: false);

        for (int i = 0; i < PermitLimit + 1; i++)
        {
            throttle.TryAcquire(sibling);
        }

        // Act
        bool[] results = Enumerable.Range(0, PermitLimit)
            .Select(_ => throttle.TryAcquire(owner))
            .ToArray();

        // Assert
        results.ShouldAllBe(acquired => acquired);
    }

    [Fact]
    public void TryAcquire_ShouldShareOneBudget_BetweenUnconfirmedAccountsAtSpellingsOfOneInbox()
    {
        // Arrange: each spelling a stranger registers must not bring a fresh budget
        using PasswordResetRequestThrottle throttle = new(PermitLimit, AccountBudgets.Window);
        ApplicationUser[] siblings =
        [
            User("ANNA+1@GMAIL.COM", emailConfirmed: false),
            User("A.NNA@GMAIL.COM", emailConfirmed: false),
            User("ANNA@GOOGLEMAIL.COM", emailConfirmed: false)
        ];
        siblings.Length.ShouldBe(PermitLimit);

        foreach (ApplicationUser sibling in siblings)
        {
            throttle.TryAcquire(sibling).ShouldBeTrue();
        }

        // Act
        bool acquired = throttle.TryAcquire(User("ANNA+2@GMAIL.COM", emailConfirmed: false));

        // Assert
        acquired.ShouldBeFalse();
    }

    [Fact]
    public void TryAcquire_ShouldGiveEveryConfirmedAccountItsOwnBudget()
    {
        // Arrange
        using PasswordResetRequestThrottle throttle = new(PermitLimit, AccountBudgets.Window);
        ApplicationUser account = User("ANNA@GMAIL.COM", emailConfirmed: true);

        // Act
        bool[] results = Enumerable.Range(0, PermitLimit + 1)
            .Select(_ => throttle.TryAcquire(account))
            .ToArray();

        // Assert
        results.Take(PermitLimit).ShouldAllBe(acquired => acquired);
        results[PermitLimit].ShouldBeFalse();
        throttle.TryAcquire(User("ANNA+LOTRO@GMAIL.COM", emailConfirmed: true)).ShouldBeTrue();
    }

    [Fact]
    public void TryAcquire_ShouldRejectAnUnconfirmedAccountWithoutANormalizedAddress()
    {
        // Arrange: Identity always stores one for an account found by its address, so this is a bug
        using PasswordResetRequestThrottle throttle = new(PermitLimit, AccountBudgets.Window);
        ApplicationUser broken = new() { Id = Guid.CreateVersion7(), EmailConfirmed = false };

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => throttle.TryAcquire(broken));
    }

    private static ApplicationUser User(string normalizedEmail, bool emailConfirmed) => new()
    {
        Id = Guid.CreateVersion7(),
        Email = normalizedEmail.ToLowerInvariant(),
        NormalizedEmail = normalizedEmail,
        EmailConfirmed = emailConfirmed
    };
}
