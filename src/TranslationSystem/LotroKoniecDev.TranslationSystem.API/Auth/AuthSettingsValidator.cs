using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.API.Auth;

internal sealed class AuthSettingsValidator : AbstractValidator<AuthSettings>
{
    public AuthSettingsValidator()
    {
        RuleFor(x => x.Issuer)
            .NotEmpty()
            .Must(BeAbsoluteHttpUrl)
            .WithMessage("Issuer must be an absolute http(s) URL.");

        RuleFor(x => x.Audience)
            .NotEmpty();

        RuleFor(x => x.Authority!)
            .Must(BeAbsoluteHttpUrl)
            .When(x => !string.IsNullOrWhiteSpace(x.Authority))
            .WithMessage("Authority must be an absolute http(s) URL.");
    }

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
