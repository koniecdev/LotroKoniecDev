using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal sealed class PasswordResetEmailSender : IPasswordResetEmailSender
{
    private readonly IEmailService _emailService;
    private readonly IPasswordResetLinkFactory _passwordResetLinkFactory;
    private readonly IEmailTemplateRenderer _templateRenderer;
    private readonly TimeSpan _linkLifespan;
    private readonly ILogger<PasswordResetEmailSender> _logger;

    public PasswordResetEmailSender(
        IEmailService emailService,
        IPasswordResetLinkFactory passwordResetLinkFactory,
        IEmailTemplateRenderer templateRenderer,
        IOptions<DataProtectionTokenProviderOptions> tokenProviderOptions,
        ILogger<PasswordResetEmailSender> logger)
    {
        _emailService = emailService;
        _passwordResetLinkFactory = passwordResetLinkFactory;
        _templateRenderer = templateRenderer;
        _linkLifespan = tokenProviderOptions.Value.TokenLifespan;
        _logger = logger;
    }

    public async Task<Result> SendPasswordResetEmailAsync(Guid userId, string email, string resetToken, CancellationToken cancellationToken)
    {
        string link = _passwordResetLinkFactory.Create(email, resetToken);

        EmailTemplateModel template = new()
        {
            Preheader = "Ustaw nowe hasło do swojego konta.",
            Heading = "Reset hasła",
            Paragraphs =
            [
                $"Otrzymaliśmy prośbę o zresetowanie hasła do Twojego konta na {EmailBranding.Name}.",
                $"Link wygasa po {EmailDurationText.Describe(_linkLifespan)} od wysłania tej wiadomości."
            ],
            CallToAction = new EmailCallToAction("Ustaw nowe hasło", link),
            SecurityNote =
                "Jeśli to nie Ty prosiłeś(-aś) o reset hasła, zignoruj tę wiadomość — Twoje hasło pozostanie bez zmian."
        };

        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["EmailOperation"] = "PasswordReset",
            ["RecipientUserId"] = userId
        });

        return await _emailService
            .SendAsync(
                receiverEmail: email,
                subject: $"Reset hasła — {EmailBranding.Name}",
                body: _templateRenderer.Render(template),
                cancellationToken: cancellationToken);
    }
}
