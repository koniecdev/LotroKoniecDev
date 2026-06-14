using FluentValidation;

namespace LotroKoniecDev.Frontend.Settings;

internal sealed class TranslationSystemSettingsValidator : AbstractValidator<TranslationSystemSettings>
{
    public TranslationSystemSettingsValidator()
    {
        RuleFor(x => x.BaseUrl)
            .NotEmpty()
            .Must(BeAbsoluteHttpUrl)
            .WithMessage($"{nameof(TranslationSystemSettings.BaseUrl)} must be an absolute http(s) URL.");
    }

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
