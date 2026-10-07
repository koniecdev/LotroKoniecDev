using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Constants;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Identity;

public sealed class PasswordMaxLengthValidatorTests
{
    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly PasswordMaxLengthValidator _sut = new();

    [Theory]
    [InlineData(PasswordConstants.MinLength)]
    [InlineData(PasswordConstants.MaxLength - 1)]
    [InlineData(PasswordConstants.MaxLength)]
    public async Task ValidateAsync_PasswordUpToTheMaximum_Succeeds(int length)
    {
        IdentityResult result = await _sut.ValidateAsync(_userManager, new ApplicationUser(), PasswordOfLength(length));

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(PasswordConstants.MaxLength + 1)]
    [InlineData(500)]
    public async Task ValidateAsync_PasswordOverTheMaximum_FailsAndNamesTheRule(int length)
    {
        IdentityResult result = await _sut.ValidateAsync(_userManager, new ApplicationUser(), PasswordOfLength(length));

        result.Succeeded.ShouldBeFalse();
        IdentityError error = result.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(PasswordMaxLengthValidator.ErrorCode);
        error.Description.ShouldBe("Passwords must be at most 128 characters.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ValidateAsync_NoPassword_Succeeds(string? password)
    {
        IdentityResult result = await _sut.ValidateAsync(_userManager, new ApplicationUser(), password);

        result.Succeeded.ShouldBeTrue();
    }

    private static string PasswordOfLength(int length) => "Aa1!" + new string('x', length - 4);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
