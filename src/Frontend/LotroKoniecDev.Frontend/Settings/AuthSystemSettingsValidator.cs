using FluentValidation;

namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// Checks the OIDC settings the Frontend logs translators in with, and stops the boot when they are
/// wrong (ADR-0008 §3, M6-05). Every environment needs them. The messages name the full configuration
/// key, so a missing or invalid value fails the boot instead of breaking the login later.
/// </summary>
internal sealed class AuthSystemSettingsValidator : AbstractValidator<AuthSystemSettings>
{
    public AuthSystemSettingsValidator()
    {
        RuleFor(x => x.BaseUrl)
            .NotEmpty()
            .Must(BeAbsoluteHttpUrl)
            .WithMessage(KeyPath(nameof(AuthSystemSettings.BaseUrl)) + " must be a non-empty absolute http(s) URL.");

        RuleFor(x => x.Authority)
            .NotEmpty()
            .Must(BeAbsoluteHttpUrl)
            .WithMessage(KeyPath(nameof(AuthSystemSettings.Authority)) + " must be a non-empty absolute http(s) URL.");

        RuleFor(x => x.ClientId)
            .NotEmpty()
            .WithMessage(KeyPath(nameof(AuthSystemSettings.ClientId)) + " is required.");

        RuleFor(x => x.CallbackPath)
            .NotEmpty()
            .Must(BeRootedPath)
            .WithMessage(KeyPath(nameof(AuthSystemSettings.CallbackPath)) + " must be a rooted path (starting with '/').");

        RuleFor(x => x.SignedOutCallbackPath)
            .NotEmpty()
            .Must(BeRootedPath)
            .WithMessage(
                KeyPath(nameof(AuthSystemSettings.SignedOutCallbackPath)) + " must be a rooted path (starting with '/').");

        RuleFor(x => x.Scopes)
            .NotEmpty()
            .WithMessage(KeyPath(nameof(AuthSystemSettings.Scopes)) + " must contain at least one scope.");
    }

    private static string KeyPath(string propertyName)
        => $"{AuthSystemSettings.ConfigurationSection}:{propertyName}";

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool BeRootedPath(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.StartsWith('/');
    }
}
