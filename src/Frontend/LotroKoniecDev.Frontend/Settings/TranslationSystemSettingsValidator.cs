using FluentValidation;

namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// Checks the TMS API settings the Frontend calls with, and stops the boot when they are wrong (ADR-0008
/// §3, M6-05). Every environment needs the base address. The messages name the full configuration key,
/// so a missing value fails the boot instead of failing the first API call later.
/// The caller key is required outside Development and Testing only (ADR-0054 §6, #823): those two run
/// against a TMS API whose limiter is off, so there is nothing for the key to decide. Anywhere else a
/// missing key would put every translator's TMS API calls back into this container's one bucket.
/// </summary>
internal sealed class TranslationSystemSettingsValidator : AbstractValidator<TranslationSystemSettings>
{
    public const int MinimumCallerKeyLength = 32;

    private const string TestingEnvironmentName = "Testing";

    public TranslationSystemSettingsValidator(IHostEnvironment environment)
    {
        RuleFor(x => x.BaseUrl)
            .NotEmpty()
            .Must(BeAbsoluteHttpUrl)
            .WithMessage(KeyPath(nameof(TranslationSystemSettings.BaseUrl)) + " must be a non-empty absolute http(s) URL.");

        RuleFor(x => x.CallerKey)
            .NotEmpty()
            .When(_ => !environment.IsDevelopment() && !environment.IsEnvironment(TestingEnvironmentName))
            .WithMessage(
                KeyPath(nameof(TranslationSystemSettings.CallerKey))
                + " must be set outside Development and Testing (FRONTEND_CALLER_KEY in the box .env, ADR-0054).");

        RuleFor(x => x.CallerKey!)
            .MinimumLength(MinimumCallerKeyLength)
            .When(x => !string.IsNullOrWhiteSpace(x.CallerKey))
            .WithMessage(
                KeyPath(nameof(TranslationSystemSettings.CallerKey))
                + $" must be at least {MinimumCallerKeyLength} characters (openssl rand -base64 32).");

        // The API trims the spaces around a header value, and HttpClient refuses to send a newline,
        // so a padded key could never match.
        RuleFor(x => x.CallerKey!)
            .Must(HaveNoWhitespaceAround)
            .When(x => !string.IsNullOrWhiteSpace(x.CallerKey))
            .WithMessage(
                KeyPath(nameof(TranslationSystemSettings.CallerKey))
                + " must not start or end with whitespace. Check the quoting of FRONTEND_CALLER_KEY in the box .env.");

        // HttpClient refuses a line break or a non-ASCII character in a header, and Caddy answers 400 to
        // every other control character except a tab. A tab would get through, but no generated key has
        // one, so the one simple rule refuses it too (#877).
        RuleFor(x => x.CallerKey!)
            .Must(BePrintableAscii)
            .When(x => !string.IsNullOrWhiteSpace(x.CallerKey))
            .WithMessage(
                KeyPath(nameof(TranslationSystemSettings.CallerKey))
                + " must contain only printable ASCII characters: no line break, tab, other control character or "
                + "non-ASCII character such as a non-breaking space. Check FRONTEND_CALLER_KEY in the box .env "
                + "(openssl rand -base64 32).");
    }

    private static string KeyPath(string propertyName)
        => $"{TranslationSystemSettings.ConfigurationSection}:{propertyName}";

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool HaveNoWhitespaceAround(string value) => value == value.Trim();

    private static bool BePrintableAscii(string value) => value.All(character => character is >= ' ' and <= '~');
}
