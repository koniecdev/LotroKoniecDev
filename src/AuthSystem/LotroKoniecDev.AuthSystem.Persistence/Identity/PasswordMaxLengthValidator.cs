using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.Persistence.Identity;

/// <summary>
/// Identity's password options have a minimum length but no maximum. Identity runs this validator on
/// every path that sets a password, so a page that calls <c>UserManager</c> directly, without the API's
/// FluentValidation rules, still keeps the limit (#1046).
/// </summary>
public sealed class PasswordMaxLengthValidator : IPasswordValidator<ApplicationUser>
{
    public const string ErrorCode = "PasswordTooLong";

    public Task<IdentityResult> ValidateAsync(
        UserManager<ApplicationUser> manager,
        ApplicationUser user,
        string? password)
    {
        if (password is null || password.Length <= PasswordConstants.MaxLength)
        {
            return Task.FromResult(IdentityResult.Success);
        }

        return Task.FromResult(IdentityResult.Failed(new IdentityError
        {
            Code = ErrorCode,
            Description = $"Passwords must be at most {PasswordConstants.MaxLength} characters."
        }));
    }
}
