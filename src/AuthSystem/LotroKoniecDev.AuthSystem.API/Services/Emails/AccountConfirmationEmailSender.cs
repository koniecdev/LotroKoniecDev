using System.Net;
using Microsoft.Extensions.Logging;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal sealed class AccountConfirmationEmailSender : IAccountConfirmationEmailSender
{
    private readonly IEmailService _emailService;
    private readonly IEmailVerificationLinkFactory _emailVerificationLinkFactory;
    private readonly ILogger<AccountConfirmationEmailSender> _logger;

    public AccountConfirmationEmailSender(
        IEmailService emailService,
        IEmailVerificationLinkFactory emailVerificationLinkFactory,
        ILogger<AccountConfirmationEmailSender> logger)
    {
        _emailService = emailService;
        _emailVerificationLinkFactory = emailVerificationLinkFactory;
        _logger = logger;
    }

    public async Task<Result> SendEmailConfirmationAsync(string email, string confirmationToken, CancellationToken cancellationToken)
    {
        string rawLink = _emailVerificationLinkFactory.Create(email, confirmationToken);
        string link = WebUtility.HtmlEncode(rawLink);

        string emailBody =
            $"Dziękujemy za rejestracje na platformie lotro-translator.pl. Prosimy o kliknięcie w ten link, aby potwierdzić konto: <a href='{link}'>link</a>";

        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["EmailOperation"] = "AccountConfirmation",
            ["Recipient"] = email.MaskEmail()
        });

        return await _emailService
            .SendAsync(
                receiverEmail: email,
                subject: "Potwierdzenie konta",
                body: emailBody,
                isBodyHtml: true,
                cancellationToken: cancellationToken);
    }
}
