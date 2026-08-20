using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal sealed class EmailChangeEmailSender : IEmailChangeEmailSender
{
    private readonly IEmailService _emailService;
    private readonly IEmailChangeVerificationLinkFactory _verificationLinkFactory;
    private readonly IEmailChangeRevertLinkFactory _revertLinkFactory;
    private readonly IEmailTemplateRenderer _templateRenderer;
    private readonly TimeSpan _verificationLinkLifespan;
    private readonly ILogger<EmailChangeEmailSender> _logger;

    public EmailChangeEmailSender(
        IEmailService emailService,
        IEmailChangeVerificationLinkFactory verificationLinkFactory,
        IEmailChangeRevertLinkFactory revertLinkFactory,
        IEmailTemplateRenderer templateRenderer,
        IOptions<EmailChangeTokenProviderOptions> tokenProviderOptions,
        ILogger<EmailChangeEmailSender> logger)
    {
        _emailService = emailService;
        _verificationLinkFactory = verificationLinkFactory;
        _revertLinkFactory = revertLinkFactory;
        _templateRenderer = templateRenderer;
        _verificationLinkLifespan = tokenProviderOptions.Value.TokenLifespan;
        _logger = logger;
    }

    public Task<Result> SendVerificationAsync(
        Guid userId,
        string newEmail,
        string verificationToken,
        CancellationToken cancellationToken)
    {
        string link = _verificationLinkFactory.Create(userId, newEmail, verificationToken);

        EmailTemplateModel template = new()
        {
            Preheader = "Potwierdź nowy adres e-mail swojego konta.",
            Heading = "Potwierdź nowy adres e-mail",
            Paragraphs =
            [
                $"Na tym adresie ma działać Twoje konto w {EmailBranding.Name}.",
                "Adres e-mail jest jednocześnie loginem, więc po potwierdzeniu będziesz logować się właśnie tym adresem.",
                $"Link wygasa po {EmailDurationText.Describe(_verificationLinkLifespan)} od wysłania tej wiadomości."
            ],
            CallToAction = new EmailCallToAction("Potwierdź nowy adres", link),
            SecurityNote =
                "Jeśli to nie Ty prosiłeś(-aś) o zmianę adresu, zignoruj tę wiadomość — bez kliknięcia w link nic się nie zmieni."
        };

        return SendAsync(
            userId,
            operation: "EmailChangeVerification",
            recipient: newEmail,
            subject: $"Potwierdź nowy adres e-mail — {EmailBranding.Name}",
            template: template,
            cancellationToken: cancellationToken);
    }

    public Task<Result> SendChangeRequestedWarningAsync(
        Guid userId,
        string currentEmail,
        string newEmail,
        CancellationToken cancellationToken)
    {
        EmailTemplateModel template = new()
        {
            Preheader = "Ktoś poprosił o zmianę adresu e-mail Twojego konta.",
            Heading = "Prośba o zmianę adresu e-mail",
            Paragraphs =
            [
                $"Ktoś, kto zna hasło do Twojego konta w {EmailBranding.Name}, poprosił o przeniesienie go na adres {newEmail}.",
                "Nic jeszcze się nie zmieniło. Adres zmieni się dopiero wtedy, gdy ktoś kliknie link wysłany na tamten adres.",
                "Jeśli to Ty — nie musisz nic robić z tą wiadomością."
            ],
            SecurityNote =
                "Jeśli to nie Ty — natychmiast zmień hasło. Ktoś inny je zna. Gdy zmiana adresu jednak dojdzie do skutku, "
                + "wyślemy tu link, którym cofniesz ją i unieważnisz to hasło."
        };

        return SendAsync(
            userId,
            operation: "EmailChangeRequestedWarning",
            recipient: currentEmail,
            subject: $"Prośba o zmianę adresu e-mail — {EmailBranding.Name}",
            template: template,
            cancellationToken: cancellationToken);
    }

    public Task<Result> SendChangedNoticeAsync(
        Guid userId,
        string newEmail,
        string previousEmail,
        CancellationToken cancellationToken)
    {
        EmailTemplateModel template = new()
        {
            Preheader = "Ten adres jest teraz Twoim loginem.",
            Heading = "Adres e-mail został zmieniony",
            Paragraphs =
            [
                $"Konto w {EmailBranding.Name} korzysta od teraz z tego adresu. Poprzedni adres to {previousEmail}.",
                "Adres e-mail jest jednocześnie loginem, więc następne logowanie wykonaj już tym adresem.",
                "Ze względów bezpieczeństwa wszystkie sesje zostały zakończone — zaloguj się ponownie."
            ],
            SecurityNote =
                "Jeśli to nie Ty zmieniałeś(-aś) adres, skorzystaj z linku wysłanego na poprzedni adres, aby cofnąć zmianę."
        };

        return SendAsync(
            userId,
            operation: "EmailChanged",
            recipient: newEmail,
            subject: $"Adres e-mail został zmieniony — {EmailBranding.Name}",
            template: template,
            cancellationToken: cancellationToken);
    }

    public Task<Result> SendChangedNoticeWithRevertAsync(
        Guid userId,
        string previousEmail,
        string newEmail,
        string revertToken,
        TimeSpan revertWindow,
        CancellationToken cancellationToken)
    {
        string link = _revertLinkFactory.Create(userId, previousEmail, newEmail, revertToken);

        EmailTemplateModel template = new()
        {
            Preheader = "Konto przeniesiono na inny adres. Jeśli to nie Ty — cofnij zmianę.",
            Heading = "Adres e-mail Twojego konta został zmieniony",
            Paragraphs =
            [
                $"Konto w {EmailBranding.Name}, które działało na tym adresie, korzysta od teraz z adresu {newEmail}.",
                "Jeśli to Ty — nie musisz nic robić.",
                $"Jeśli to nie Ty, użyj przycisku poniżej. Cofnie on zmianę, przywróci ten adres i unieważni obecne hasło, "
                + $"a następnie pozwoli Ci ustawić nowe. Link działa przez {EmailDurationText.Describe(revertWindow)} od wysłania tej wiadomości."
            ],
            CallToAction = new EmailCallToAction("To nie ja — cofnij zmianę", link),
            SecurityNote =
                "Po upływie tego czasu odzyskanie konta będzie wymagało kontaktu z administratorem."
        };

        return SendAsync(
            userId,
            operation: "EmailChangedRevertOffer",
            recipient: previousEmail,
            subject: $"Adres e-mail Twojego konta został zmieniony — {EmailBranding.Name}",
            template: template,
            cancellationToken: cancellationToken);
    }

    private async Task<Result> SendAsync(
        Guid userId,
        string operation,
        string recipient,
        string subject,
        EmailTemplateModel template,
        CancellationToken cancellationToken)
    {
        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["EmailOperation"] = operation,
            ["RecipientUserId"] = userId
        });

        return await _emailService
            .SendAsync(
                receiverEmail: recipient,
                subject: subject,
                body: _templateRenderer.Render(template),
                cancellationToken: cancellationToken);
    }
}
