using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using LotroKoniecDev.AuthSystem.Infrastructure.Errors;
using LotroKoniecDev.SharedKernel.Monads;
using AuthenticationException = System.Security.Authentication.AuthenticationException;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Emails;

internal sealed partial class EmailService : IEmailService
{
    private readonly EmailOptions _options;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<EmailOptions> options, ILogger<EmailService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result> SendAsync(
        string receiverEmail,
        string subject,
        string body,
        bool isBodyHtml = true,
        CancellationToken cancellationToken = default)
    {
        using MimeMessage message = BuildMessage(receiverEmail, subject, body, isBodyHtml);
        SecureSocketOptions secureOptions = MapSecurityMode(_options.Mode);

        Exception? lastTransientError = null;

        for (int attempt = 1; attempt <= _options.MaxSendAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await SendOnceAsync(message, secureOptions, cancellationToken);
                return Result.Success();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AuthenticationException ex)
            {
                LogEmailAuthenticationError(_logger, ex, subject);
                return Result.Failure(InfrastructureErrors.Email.SendFailed(ex.Message));
            }
            catch (Exception ex) when (
                ex is ProtocolException
                    or ServiceNotConnectedException
                    or ServiceNotAuthenticatedException
                    or SocketException
                    or IOException)
            {
                lastTransientError = ex;
                LogEmailTransportError(_logger, ex, subject, attempt, _options.MaxSendAttempts);
            }
            catch (Exception ex)
            {
                LogEmailUnexpectedError(_logger, ex, subject);
                return Result.Failure(
                    InfrastructureErrors.Email.SendFailed(
                        "An unexpected error occurred while sending the email."));
            }

            if (attempt < _options.MaxSendAttempts)
            {
                await Task.Delay(GetBackoff(attempt), cancellationToken);
            }
        }

        return Result.Failure(InfrastructureErrors.Email.SendFailed(
            lastTransientError?.Message ?? "Email send failed after retries."));
    }

    private MimeMessage BuildMessage(string receiverEmail, string subject, string body, bool isBodyHtml)
    {
        MimeMessage message = new();
        message.From.Add(new MailboxAddress(_options.Sender, _options.SenderEmail));
        message.To.Add(MailboxAddress.Parse(receiverEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder
        {
            HtmlBody = isBodyHtml ? body : null,
            TextBody = isBodyHtml ? null : body
        }.ToMessageBody();
        return message;
    }

    private async Task SendOnceAsync(
        MimeMessage message,
        SecureSocketOptions secureOptions,
        CancellationToken cancellationToken)
    {
        using SmtpClient smtp = new() { Timeout = _options.TimeoutSeconds * 1000 };
        try
        {
            await smtp.ConnectAsync(_options.Host, _options.Port, secureOptions, cancellationToken);

            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                string password = _options.Password
                    ?? throw new InvalidOperationException(
                        $"{nameof(EmailOptions.Password)} must be configured when {nameof(EmailOptions.Username)} is set.");
                await smtp.AuthenticateAsync(_options.Username, password, cancellationToken);
            }

            await smtp.SendAsync(message, cancellationToken);
        }
        finally
        {
            if (smtp.IsConnected)
            {
                try
                {
                    await smtp.DisconnectAsync(true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LogEmailDisconnectWarning(_logger, ex);
                }
            }
        }
    }

    private static SecureSocketOptions MapSecurityMode(EmailSecurityMode mode) => mode switch
    {
        EmailSecurityMode.None => SecureSocketOptions.None,
        EmailSecurityMode.StartTls => SecureSocketOptions.StartTls,
        EmailSecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.StartTls
    };

    private static TimeSpan GetBackoff(int attempt)
    {
        int baseMs = 500 * (1 << (attempt - 1));
        int jitter = Random.Shared.Next(0, 250);
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }

    [LoggerMessage(
        EventId = EventIds.EmailTransportError,
        Level = LogLevel.Warning,
        Message = "Email send transport failure for subject '{Subject}' on attempt {Attempt} of {MaxAttempts}")]
    private static partial void LogEmailTransportError(
        ILogger logger,
        Exception exception,
        string subject,
        int attempt,
        int maxAttempts);

    [LoggerMessage(
        EventId = EventIds.EmailAuthenticationError,
        Level = LogLevel.Error,
        Message = "SMTP authentication failed while sending email with subject '{Subject}'")]
    private static partial void LogEmailAuthenticationError(ILogger logger, Exception exception, string subject);

    [LoggerMessage(
        EventId = EventIds.EmailUnexpectedError,
        Level = LogLevel.Error,
        Message = "Unexpected error sending email with subject '{Subject}'")]
    private static partial void LogEmailUnexpectedError(ILogger logger, Exception exception, string subject);

    [LoggerMessage(
        EventId = EventIds.EmailDisconnectWarning,
        Level = LogLevel.Debug,
        Message = "Failed to cleanly disconnect SMTP session after send")]
    private static partial void LogEmailDisconnectWarning(ILogger logger, Exception exception);
}
