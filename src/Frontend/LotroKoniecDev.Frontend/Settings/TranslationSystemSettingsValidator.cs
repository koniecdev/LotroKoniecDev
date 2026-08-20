using FluentValidation;

namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// Checks the TMS API base address the Frontend calls, and stops the boot when it is missing (ADR-0008
/// §3, M6-05). Every environment needs it. The message names the full configuration key, so a missing
/// value fails the boot instead of failing the first API call later.
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
