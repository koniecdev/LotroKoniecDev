using System.Net;
using Microsoft.Extensions.Logging;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal sealed class PasswordResetEmailSender : IPasswordResetEmailSender
{
    private readonly IEmailService _emailService;
    private readonly IPasswordResetLinkFactory _passwordResetLinkFactory;
    private readonly ILogger<PasswordResetEmailSender> _logger;

    public PasswordResetEmailSender(
        IEmailService emailService,
        IPasswordResetLinkFactory passwordResetLinkFactory,
        ILogger<PasswordResetEmailSender> logger)
    {
        _emailService = emailService;
        _passwordResetLinkFactory = passwordResetLinkFactory;
        _logger = logger;
    }

    public async Task<Result> SendPasswordResetEmailAsync(Guid userId, string email, string resetToken, CancellationToken cancellationToken)
    {
        string rawLink = _passwordResetLinkFactory.Create(email, resetToken);
        string link = WebUtility.HtmlEncode(rawLink);

        string emailBody =
            $"Otrzymaliśmy prośbę o zresetowanie hasła do Twojego konta na lotro-translator.pl. Kliknij w poniższy link, aby ustawić nowe hasło: <a href='{link}'>link</a>";

        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["EmailOperation"] = "PasswordReset",
            ["RecipientUserId"] = userId
        });

        return await _emailService
            .SendAsync(
                receiverEmail: email,
                subject: "Resetowanie hasła",
                body: emailBody,
                isBodyHtml: true,
                cancellationToken: cancellationToken);
    }
}
