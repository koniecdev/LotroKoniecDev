using System.Globalization;
using System.Net;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal sealed class AccountDeletionEmailSender : IAccountDeletionEmailSender
{
    private readonly IEmailService _emailService;
    private readonly ICancelDeletionLinkFactory _cancelDeletionLinkFactory;
    private readonly ILogger<AccountDeletionEmailSender> _logger;

    public AccountDeletionEmailSender(
        IEmailService emailService,
        ICancelDeletionLinkFactory cancelDeletionLinkFactory,
        ILogger<AccountDeletionEmailSender> logger)
    {
        _emailService = emailService;
        _cancelDeletionLinkFactory = cancelDeletionLinkFactory;
        _logger = logger;
    }

    public async Task<Result> SendDeletionScheduledEmailAsync(
        string email,
        string cancelToken,
        DateTimeOffset finalizesAt,
        CancellationToken cancellationToken)
    {
        string rawLink = _cancelDeletionLinkFactory.Create(email, cancelToken);
        string link = WebUtility.HtmlEncode(rawLink);
        string deletionDate = finalizesAt.ToPolandTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string emailBody =
            $"<p>Otrzymaliśmy prośbę o usunięcie Twojego konta na lotro-translator.pl. " +
            $"Konto zostanie trwale usunięte dnia <strong>{deletionDate}</strong>. " +
            $"Do tego czasu konto pozostaje zablokowane.</p>" +
            $"<p>Jeśli to nie Ty złożyłeś(-aś) tę prośbę, kliknij w poniższy link, aby anulować " +
            $"usunięcie konta i ustawić nowe hasło: <a href='{link}'>anuluj usunięcie konta</a></p>";

        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["EmailOperation"] = "AccountDeletionScheduled",
            ["Recipient"] = email.MaskEmail()
        });

        return await _emailService
            .SendAsync(
                receiverEmail: email,
                subject: "Zaplanowano usunięcie konta",
                body: emailBody,
                isBodyHtml: true,
                cancellationToken: cancellationToken);
    }

    public async Task<Result> SendDeletionCancelledEmailAsync(
        string email,
        CancellationToken cancellationToken)
    {
        const string emailBody =
            "<p>Usunięcie Twojego konta na lotro-translator.pl zostało anulowane. " +
            "Ze względów bezpieczeństwa dotychczasowe hasło przestało działać — " +
            "ustaw nowe hasło, korzystając z formularza resetu hasła.</p>";

        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["EmailOperation"] = "AccountDeletionCancelled",
            ["Recipient"] = email.MaskEmail()
        });

        return await _emailService
            .SendAsync(
                receiverEmail: email,
                subject: "Anulowano usunięcie konta",
                body: emailBody,
                isBodyHtml: true,
                cancellationToken: cancellationToken);
    }
}
