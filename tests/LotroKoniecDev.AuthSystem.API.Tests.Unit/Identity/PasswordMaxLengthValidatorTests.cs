using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
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

    /// <summary>
    /// The API refuses an over-long password with FluentValidation and the reset page with this validator.
    /// Both must count the same way, or one path takes a password the other refuses. An emoji is two
    /// UTF-16 code units, and both count those.
    /// </summary>
    public static TheoryData<string> PasswordsAroundTheMaximum => new()
    {
        PasswordOfLength(PasswordConstants.MaxLength),
        PasswordOfLength(PasswordConstants.MaxLength + 1),
        "Aa1!" + string.Concat(Enumerable.Repeat("😀", (PasswordConstants.MaxLength - 4) / 2)),
        "Aa1!" + string.Concat(Enumerable.Repeat("😀", (PasswordConstants.MaxLength - 4) / 2 + 1))
    };

    [Theory]
    [MemberData(nameof(PasswordsAroundTheMaximum))]
    public async Task ValidateAsync_PasswordAroundTheMaximum_RefusesExactlyWhenTheApiRuleRefusesItsLength(string password)
    {
        ValidationResult apiResult =
            await new ApiPasswordRules().ValidateAsync(new PasswordInput(password));
        bool apiRefusesTheLength = apiResult.Errors.Any(error => error.ErrorCode is "MaximumLengthValidator");

        IdentityResult result = await _sut.ValidateAsync(_userManager, new ApplicationUser(), password);

        result.Succeeded.ShouldBe(!apiRefusesTheLength);
    }

    private static string PasswordOfLength(int length) => "Aa1!" + new string('x', length - 4);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);

    private sealed record PasswordInput(string Password);

    private sealed class ApiPasswordRules : AbstractValidator<PasswordInput>
    {
        public ApiPasswordRules()
        {
            RuleFor(input => input.Password).ApplyPasswordRules();
        }
    }
}
