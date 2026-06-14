using FluentValidation;

namespace LotroKoniecDev.Frontend.Settings;

internal sealed class AuthSystemSettingsValidator : AbstractValidator<AuthSystemSettings>
{
    public AuthSystemSettingsValidator()
    {
        RuleFor(x => x.BaseUrl)
            .NotEmpty()
            .Must(BeAbsoluteHttpUrl)
            .WithMessage($"{nameof(AuthSystemSettings.BaseUrl)} must be an absolute http(s) URL.");

        RuleFor(x => x.Authority)
            .NotEmpty()
            .Must(BeAbsoluteHttpUrl)
            .WithMessage($"{nameof(AuthSystemSettings.Authority)} must be an absolute http(s) URL.");

        RuleFor(x => x.ClientId)
            .NotEmpty();

        RuleFor(x => x.CallbackPath)
            .NotEmpty()
            .Must(BeRootedPath)
            .WithMessage($"{nameof(AuthSystemSettings.CallbackPath)} must be a rooted path (starting with '/').");

        RuleFor(x => x.SignedOutCallbackPath)
            .NotEmpty()
            .Must(BeRootedPath)
            .WithMessage($"{nameof(AuthSystemSettings.SignedOutCallbackPath)} must be a rooted path (starting with '/').");

        RuleFor(x => x.Scopes)
            .NotEmpty();
    }

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
