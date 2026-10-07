using FluentValidation;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal static class PasswordValidationRules
{
    public static IRuleBuilderOptions<T, string> ApplyPasswordRules<T>(
        this IRuleBuilder<T, string> ruleBuilder)
    {
        return ruleBuilder
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(PasswordConstants.MinLength)
                .WithMessage($"Password must be at least {PasswordConstants.MinLength} characters long.")
            .MaximumLength(PasswordConstants.MaxLength)
                .WithMessage($"Password must not exceed {PasswordConstants.MaxLength} characters.")
            .Matches("[0-9]").WithMessage("Password must contain at least one digit.")
            .Matches("[a-z]").WithMessage("Password must contain at least one lowercase letter.")
            .Matches("[A-Z]").WithMessage("Password must contain at least one uppercase letter.")
            .Matches("[^a-zA-Z0-9]").WithMessage("Password must contain at least one special character.");
    }
}
