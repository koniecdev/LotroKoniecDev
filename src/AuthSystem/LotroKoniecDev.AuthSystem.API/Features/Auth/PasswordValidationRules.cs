using FluentValidation;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal static class PasswordValidationRules
{
    private const int PasswordMinLength = 8;
    private const int PasswordMaxLength = 128;

    public static IRuleBuilderOptions<T, string> ApplyPasswordRules<T>(
        this IRuleBuilder<T, string> ruleBuilder)
    {
        return ruleBuilder
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(PasswordMinLength)
                .WithMessage($"Password must be at least {PasswordMinLength} characters long.")
            .MaximumLength(PasswordMaxLength)
                .WithMessage($"Password must not exceed {PasswordMaxLength} characters.")
            .Matches("[0-9]").WithMessage("Password must contain at least one digit.")
            .Matches("[a-z]").WithMessage("Password must contain at least one lowercase letter.")
            .Matches("[A-Z]").WithMessage("Password must contain at least one uppercase letter.")
            .Matches("[^a-zA-Z0-9]").WithMessage("Password must contain at least one special character.");
    }
}
