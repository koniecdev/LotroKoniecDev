using System.Diagnostics.CodeAnalysis;
using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.API.Auth;

/// <summary>
/// Checks the JWT bearer settings the TMS uses to validate AuthSystem (OpenIddict) tokens, and stops
/// the boot when they are wrong (ADR-0008 §3, M6-05). Issuer and Audience are required in every
/// environment, because the dev compose stack and the test harness both need an issuer, so there is no
/// exception. The messages name the full configuration key, so a missing value fails the boot instead
/// of rejecting every request later.
/// </summary>
internal sealed class AuthSettingsValidator : AbstractValidator<AuthSettings>
{
    public AuthSettingsValidator()
    {
        RuleFor(x => x.Issuer)
            .NotEmpty()
            .WithMessage(KeyPath(nameof(AuthSettings.Issuer)) + " is required.")
            .Must(BeAbsoluteHttpUrl)
            .WithMessage(KeyPath(nameof(AuthSettings.Issuer)) + " must be an absolute http(s) URL.")
            .Must(NotCarryUserInfo)
            .WithMessage(UserInfoMessage(nameof(AuthSettings.Issuer)));

        RuleFor(x => x.Audience)
            .NotEmpty()
            .WithMessage(KeyPath(nameof(AuthSettings.Audience)) + " is required.");

        RuleFor(x => x.Authority!)
            .Must(BeAbsoluteHttpUrl)
            .WithMessage(KeyPath(nameof(AuthSettings.Authority)) + " must be an absolute http(s) URL.")
            .Must(NotCarryUserInfo)
            .WithMessage(UserInfoMessage(nameof(AuthSettings.Authority)))
            .When(x => !string.IsNullOrWhiteSpace(x.Authority));
    }

    private static string KeyPath(string propertyName)
        => $"{AuthSettings.ConfigurationSection}:{propertyName}";

    private static string UserInfoMessage(string propertyName)
        => KeyPath(propertyName) + " must not carry a user name or password. Some error messages print "
           + "this address in full, and they go to the log. Remove the 'user:password@' part.";

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return TryParseAbsoluteHttpUrl(value, out _);
    }

    /// <summary>
    /// The API reads the sign-in server's keys without any user name or password. But the error for a
    /// failed read of those keys prints the address in full, and so does the error for a token from the
    /// wrong issuer. The log goes to Grafana, so a user name or password here can only be a mistake that
    /// puts the password in the log (#1156, ADR-0067 decision 4). This validator checks these addresses
    /// in every environment, and no environment needs a user name or password in them, so the rule runs
    /// in every environment too (ADR-0008 §3, point 6). A value that is not an http(s) URL passes here,
    /// because the rule before this one already refuses it, and one message per key is enough.
    /// </summary>
    private static bool NotCarryUserInfo(string value)
    {
        return !TryParseAbsoluteHttpUrl(value, out Uri? uri) || string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool TryParseAbsoluteHttpUrl(string value, [NotNullWhen(true)] out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        uri = null;
        return false;
    }
}
