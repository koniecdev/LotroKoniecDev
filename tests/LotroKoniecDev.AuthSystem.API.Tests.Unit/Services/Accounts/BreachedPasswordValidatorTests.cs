using Microsoft.AspNetCore.Identity;
using NSubstitute;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Accounts;

public sealed class BreachedPasswordValidatorTests
{
    private const string Password = "Password1!";

    private readonly IPwnedPasswordChecker _pwnedPasswordChecker = Substitute.For<IPwnedPasswordChecker>();
    private readonly UserManager<ApplicationUser> _userManager =
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(), null, null, null, null, null, null, null, null);

    [Fact]
    public async Task ValidateAsync_WhenThePasswordIsInABreach_FailsWithTheBreachCode()
    {
        // Arrange
        _pwnedPasswordChecker.CheckAsync(Password, Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Breached);
        BreachedPasswordValidator validator = new(_pwnedPasswordChecker);

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), Password);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe(BreachedPasswordValidator.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_WhenThePasswordIsInNoKnownBreach_Succeeds()
    {
        // Arrange
        _pwnedPasswordChecker.CheckAsync(Password, Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.NotFound);
        BreachedPasswordValidator validator = new(_pwnedPasswordChecker);

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), Password);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    /// <summary>
    /// The failure policy of ADR-0065: an unknown answer lets the password through instead of blocking
    /// registration, password change and reset while the service is down.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheServiceIsUnavailable_SucceedsSoTheFormStillWorks()
    {
        // Arrange
        _pwnedPasswordChecker.CheckAsync(Password, Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Unavailable);
        BreachedPasswordValidator validator = new(_pwnedPasswordChecker);

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), Password);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ValidateAsync_WhenThereIsNoPassword_SucceedsAndLeavesItToTheBuiltInRules(string? password)
    {
        // Arrange: even a checker that calls everything breached must not be what refuses an empty password
        _pwnedPasswordChecker.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Breached);
        BreachedPasswordValidator validator = new(_pwnedPasswordChecker);

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), password);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }
}
