using FluentValidation;

namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// Fail-fast startup validation of the TMS API base address the Frontend calls (ADR-0008 §3, M6-05).
/// Required in every environment; the message names the full configuration key so a missing value
/// aborts boot rather than failing the first API call at runtime.
/// </summary>
internal sealed class TranslationSystemSettingsValidator : AbstractValidator<TranslationSystemSettings>
{
    public TranslationSystemSettingsValidator()
    {
        RuleFor(x => x.BaseUrl)
            .NotEmpty()
            .Must(BeAbsoluteHttpUrl)
            .WithMessage(
                $"{TranslationSystemSettings.ConfigurationSection}:{nameof(TranslationSystemSettings.BaseUrl)} "
                + "must be a non-empty absolute http(s) URL.");
    }

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
