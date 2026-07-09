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

    // Plain http hands the downloaded file to any on-path attacker (AUDIT-SEC-01 / #391), so only
    // loopback — where no network hop exists — may skip TLS (local dev against a host Kestrel).
    private static bool BeAnAbsoluteHttpsUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
}
