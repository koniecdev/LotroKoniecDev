using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.API.Auth;

/// <summary>
/// Fail-fast startup validation of the JWT bearer settings the TMS uses to validate AuthSystem
/// (OpenIddict) tokens (ADR-0008 §3, M6-05). Issuer + Audience are required in every environment —
/// the dev compose and Testing harness both need a configured issuer — so the rules are
/// unconditional; messages name the full configuration key so a missing value aborts boot rather
/// than rejecting every request at runtime.
/// </summary>
internal sealed class AuthSettingsValidator : AbstractValidator<AuthSettings>
{
    public AuthSettingsValidator()
    {
        RuleFor(x => x.Issuer)
            .NotEmpty()
            .WithMessage(KeyPath(nameof(AuthSettings.Issuer)) + " is required.")
            .Must(BeAbsoluteHttpUrl)
            .WithMessage(KeyPath(nameof(AuthSettings.Issuer)) + " must be an absolute http(s) URL.");

        RuleFor(x => x.Audience)
            .NotEmpty()
            .WithMessage(KeyPath(nameof(AuthSettings.Audience)) + " is required.");

        RuleFor(x => x.Authority!)
            .Must(BeAbsoluteHttpUrl)
            .When(x => !string.IsNullOrWhiteSpace(x.Authority))
            .WithMessage(KeyPath(nameof(AuthSettings.Authority)) + " must be an absolute http(s) URL.");
    }

    private static string KeyPath(string propertyName)
        => $"{AuthSettings.ConfigurationSection}:{propertyName}";

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
