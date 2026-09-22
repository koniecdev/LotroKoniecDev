using FluentValidation;

namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// Checks the OIDC settings the Frontend logs translators in with, and stops the boot when they are
/// wrong (ADR-0008 §3, M6-05). Every environment needs them. The messages name the full configuration
/// key, so a missing or invalid value fails the boot instead of breaking the login later.
/// The caller key is required outside Development and Testing only (ADR-0054 §6): those two run
/// against an auth API whose limiter is off, so there is nothing for the key to decide. Anywhere else a
/// missing key would put every visitor's auth API calls back into this container's one bucket.
/// </summary>
internal sealed class AuthSystemSettingsValidator : AbstractValidator<AuthSystemSettings>
{
    public const int MinimumCallerKeyLength = 32;

    private const string TestingEnvironmentName = "Testing";

    public AuthSystemSettingsValidator(IHostEnvironment environment)
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

        RuleFor(x => x.CallerKey)
            .NotEmpty()
            .When(_ => !environment.IsDevelopment() && !environment.IsEnvironment(TestingEnvironmentName))
            .WithMessage(
                KeyPath(nameof(AuthSystemSettings.CallerKey))
                + " must be set outside Development and Testing (FRONTEND_CALLER_KEY in the box .env, ADR-0054).");

        RuleFor(x => x.CallerKey!)
            .MinimumLength(MinimumCallerKeyLength)
            .When(x => !string.IsNullOrWhiteSpace(x.CallerKey))
            .WithMessage(
                KeyPath(nameof(AuthSystemSettings.CallerKey))
                + $" must be at least {MinimumCallerKeyLength} characters (openssl rand -base64 32).");
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
