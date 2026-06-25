using FluentValidation;

namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

public sealed class SyncTranslationFileCommandValidator : AbstractValidator<SyncTranslationFileCommand>
{
    public SyncTranslationFileCommandValidator()
    {
        RuleFor(command => command.TmsBaseUrl)
            .NotEmpty()
            .Must(BeAnAbsoluteHttpUrl)
            .WithMessage("The TMS base URL must be a valid absolute http(s) URL.");

        RuleFor(command => command.TranslationFilePath)
            .NotEmpty();
    }

    private static bool BeAnAbsoluteHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
