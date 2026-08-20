using FluentValidation;

namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

public sealed class SyncTranslationFileCommandValidator : AbstractValidator<SyncTranslationFileCommand>
{
    public SyncTranslationFileCommandValidator()
    {
        RuleFor(command => command.TmsBaseUrl)
            .NotEmpty()
            .Must(BeAnAbsoluteHttpsUrl)
            .WithMessage("The TMS base URL must be a valid absolute https URL (plain http is allowed only for localhost).");

        RuleFor(command => command.TranslationFilePath)
            .NotEmpty();
    }

    // Over plain http anyone on the path can change the downloaded file (AUDIT-SEC-01, #391). Only
    // loopback may skip TLS, because there is no network hop there. That covers local development
    // against a Kestrel on the same machine.
    private static bool BeAnAbsoluteHttpsUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
}
