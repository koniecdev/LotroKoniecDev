using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The API checks a new password with FluentValidation first. The reset page and the admin seed reach
/// only Identity's password validators. Both must accept and refuse the same passwords, or one path takes
/// a password the other refuses (#1046). A rule added to only one of them fails here.
/// </summary>
[Collection("AuthApi")]
public sealed class PasswordRulesParityTests
{
    private readonly AuthSystemApiFactory _factory;

    public PasswordRulesParityTests(AuthSystemApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Each rule, its boundaries, and characters outside ASCII. An emoji is two UTF-16 code units, and
    /// both sides must count it that way.
    /// </summary>
    public static TheoryData<string> Passwords => new()
    {
        "",
        "        ",
        "Abcde1!",
        "Abcdef1!",
        "abcdefg1!",
        "ABCDEFG1!",
        "Abcdefgh!",
        "Abcdefg12",
        "Abc def1",
        "Zażółć1!",
        "Zażółćgęślą1",
        "ŻÓŁĆżółć1",
        "Abcdefg١!",
        PasswordOfLength(PasswordConstants.MaxLength),
        PasswordOfLength(PasswordConstants.MaxLength + 1),
        "Aa1!" + string.Concat(Enumerable.Repeat("😀", (PasswordConstants.MaxLength - 4) / 2)),
        "Aa1!" + string.Concat(Enumerable.Repeat("😀", (PasswordConstants.MaxLength - 4) / 2 + 1))
    };

    [Theory]
    [MemberData(nameof(Passwords))]
    public async Task PasswordValidators_AnyPassword_AcceptExactlyWhatTheApiRulesAccept(string password)
    {
        ValidationResult apiResult = await new ApiPasswordRules().ValidateAsync(new PasswordInput(password));
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        List<IdentityResult> identityResults = [];

        foreach (IPasswordValidator<ApplicationUser> validator in userManager.PasswordValidators)
        {
            identityResults.Add(await validator.ValidateAsync(userManager, new ApplicationUser(), password));
        }

        identityResults.All(result => result.Succeeded).ShouldBe(apiResult.IsValid);
    }

    private static string PasswordOfLength(int length) => "Aa1!" + new string('x', length - 4);

    private sealed record PasswordInput(string Password);

    private sealed class ApiPasswordRules : AbstractValidator<PasswordInput>
    {
        public ApiPasswordRules()
        {
            RuleFor(input => input.Password).ApplyPasswordRules();
        }
    }
}
