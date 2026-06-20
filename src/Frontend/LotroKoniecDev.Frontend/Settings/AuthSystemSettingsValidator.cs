using FluentValidation;

namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// Fail-fast startup validation of the OIDC relying-party configuration the Frontend authenticates
/// translators with (ADR-0008 §3, M6-05). Required in every environment; messages name the full
/// configuration key so a missing/invalid value aborts boot rather than breaking the login flow at
/// runtime.
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
