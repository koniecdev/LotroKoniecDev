using System.Globalization;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal sealed class AccountDeletionEmailSender : IAccountDeletionEmailSender
{
    private readonly IEmailService _emailService;
    private readonly ICancelDeletionLinkFactory _cancelDeletionLinkFactory;
    private readonly IEmailTemplateRenderer _templateRenderer;
    private readonly ILogger<AccountDeletionEmailSender> _logger;

    public AccountDeletionEmailSender(
        IEmailService emailService,
        ICancelDeletionLinkFactory cancelDeletionLinkFactory,
        IEmailTemplateRenderer templateRenderer,
        ILogger<AccountDeletionEmailSender> logger)
    {
        _emailService = emailService;
        _cancelDeletionLinkFactory = cancelDeletionLinkFactory;
        _templateRenderer = templateRenderer;
        _logger = logger;
    }

    public async Task<Result> SendDeletionScheduledEmailAsync(
        Guid userId,
        string email,
        string cancelToken,
        DateTimeOffset finalizesAt,
        CancellationToken cancellationToken)
    {
        string link = _cancelDeletionLinkFactory.Create(email, cancelToken);
        string deletionDate = finalizesAt.ToPolandTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        EmailTemplateModel template = new()
        {
            Preheader = $"Konto zostanie trwale usunięte {deletionDate}.",
            Heading = "Zaplanowano usunięcie konta",
            Paragraphs =
            [
                $"Otrzymaliśmy prośbę o usunięcie Twojego konta na {EmailBranding.Name}.",
                $"Konto zostanie trwale usunięte dnia {deletionDate}. Do tego czasu pozostaje zablokowane, a usunięcie możesz anulować."
            ],
            CallToAction = new EmailCallToAction("Anuluj usunięcie konta", link),
            SecurityNote =
                "Jeśli to nie Ty złożyłeś(-aś) tę prośbę, użyj powyższego przycisku — anuluje on usunięcie i pozwoli ustawić nowe hasło."
        };

        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["EmailOperation"] = "AccountDeletionScheduled",
            ["RecipientUserId"] = userId
        });

        return await _emailService
            .SendAsync(
                receiverEmail: email,
                subject: $"Zaplanowano usunięcie konta — {EmailBranding.Name}",
                body: _templateRenderer.Render(template),
                cancellationToken: cancellationToken);
    }

    public async Task<Result> SendDeletionCancelledEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken)
    {
        EmailTemplateModel template = new()
        {
            Preheader = "Twoje konto zostało zachowane.",
            Heading = "Anulowano usunięcie konta",
            Paragraphs =
            [
                $"Usunięcie Twojego konta na {EmailBranding.Name} zostało anulowane.",
                "Ze względów bezpieczeństwa dotychczasowe hasło przestało działać — ustaw nowe, korzystając z formularza resetu hasła."
            ],
            SecurityNote =
                "Jeśli to nie Ty anulowałeś(-aś) usunięcie konta, natychmiast zresetuj hasło."
        };

        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["EmailOperation"] = "AccountDeletionCancelled",
            ["RecipientUserId"] = userId
        });

        return await _emailService
            .SendAsync(
                receiverEmail: email,
                subject: $"Anulowano usunięcie konta — {EmailBranding.Name}",
                body: _templateRenderer.Render(template),
                cancellationToken: cancellationToken);
    }
}
