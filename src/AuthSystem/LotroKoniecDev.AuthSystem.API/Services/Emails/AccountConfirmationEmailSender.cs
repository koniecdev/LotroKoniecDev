using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal sealed class AccountConfirmationEmailSender : IAccountConfirmationEmailSender
{
    private readonly IEmailService _emailService;
    private readonly IEmailVerificationLinkFactory _emailVerificationLinkFactory;
    private readonly IEmailTemplateRenderer _templateRenderer;
    private readonly TimeSpan _linkLifespan;
    private readonly ILogger<AccountConfirmationEmailSender> _logger;

    public AccountConfirmationEmailSender(
        IEmailService emailService,
        IEmailVerificationLinkFactory emailVerificationLinkFactory,
        IEmailTemplateRenderer templateRenderer,
        IOptions<DataProtectionTokenProviderOptions> tokenProviderOptions,
        ILogger<AccountConfirmationEmailSender> logger)
    {
        _emailService = emailService;
        _emailVerificationLinkFactory = emailVerificationLinkFactory;
        _templateRenderer = templateRenderer;
        _linkLifespan = tokenProviderOptions.Value.TokenLifespan;
        _logger = logger;
    }

    public async Task<Result> SendEmailConfirmationAsync(
        Guid userId,
        string email,
        string confirmationToken,
        CancellationToken cancellationToken)
    {
        string link = _emailVerificationLinkFactory.Create(email, confirmationToken);

        EmailTemplateModel template = new()
        {
            Preheader = "Potwierdź adres e-mail, aby aktywować konto.",
            Heading = "Potwierdź swoje konto",
            Paragraphs =
            [
                $"Dziękujemy za rejestrację na {EmailBranding.Name}.",
                $"Potwierdź adres e-mail, aby aktywować konto. Link wygasa po {EmailDurationText.Describe(_linkLifespan)} od wysłania tej wiadomości."
            ],
            CallToAction = new EmailCallToAction("Potwierdź konto", link),
            SecurityNote =
                "Jeśli to nie Ty zakładałeś(-aś) konto, zignoruj tę wiadomość — bez potwierdzenia konto nie zostanie aktywowane."
        };

        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["EmailOperation"] = "AccountConfirmation",
            ["RecipientUserId"] = userId
        });

        Result sendResult = await _emailService
            .SendAsync(
                receiverEmail: email,
                subject: $"Potwierdź konto — {EmailBranding.Name}",
                body: _templateRenderer.Render(template),
                cancellationToken: cancellationToken);

        return sendResult;
    }

    public async Task<Result> SendRegistrationAttemptedNoticeAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken)
    {
        EmailTemplateModel template = new()
        {
            Preheader = "Ktoś próbował założyć konto na ten adres e-mail.",
            Heading = "Ten adres ma już konto",
            Paragraphs =
            [
                $"Ktoś próbował założyć nowe konto na {EmailBranding.Name}, podając ten adres e-mail. Ten adres ma już konto, więc nowe nie powstało.",
                "Jeśli to Ty — po prostu się zaloguj. Jeśli nie pamiętasz hasła, wybierz na stronie logowania „Nie pamiętam hasła”. Jeśli konto nie jest jeszcze aktywne, zaloguj się, a strona pozwoli wysłać link aktywacyjny ponownie."
            ],
            SecurityNote =
                "Jeśli to nie Ty — nie musisz nic robić. Twoje konto się nie zmieniło."
        };

        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["EmailOperation"] = "RegistrationAttemptedNotice",
            ["RecipientUserId"] = userId
        });

        return await _emailService
            .SendAsync(
                receiverEmail: email,
                subject: $"Próba założenia konta na Twój adres — {EmailBranding.Name}",
                body: _templateRenderer.Render(template),
                cancellationToken: cancellationToken);
    }
}
