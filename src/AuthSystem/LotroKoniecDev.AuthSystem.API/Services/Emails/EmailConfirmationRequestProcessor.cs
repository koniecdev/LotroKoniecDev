using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The business reaction to a consumed <see cref="EmailConfirmationRequested"/> message: load the
/// user, mint the confirmation token at send time (see the payload's remarks on token lifetime),
/// and dispatch the e-mail. The first entry of the <see cref="IEmailMessageProcessor"/> registry
/// (ADR-0038).
/// </summary>
/// <remarks>
/// Delivery is at-least-once (ADR-0035), so this must stay idempotent. Today that comes free:
/// a redelivered message finds <see cref="ApplicationUser.EmailConfirmed"/> already set, or at
/// worst re-sends a confirmation e-mail — annoying, never harmful. A new message type must
/// re-earn this property before it may reuse the pattern.
/// </remarks>
internal sealed partial class EmailConfirmationRequestProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAccountConfirmationEmailSender _accountConfirmationEmailSender;
    private readonly ILogger<EmailConfirmationRequestProcessor> _logger;

    public EmailConfirmationRequestProcessor(
        UserManager<ApplicationUser> userManager,
        IAccountConfirmationEmailSender accountConfirmationEmailSender,
        ILogger<EmailConfirmationRequestProcessor> logger)
    {
        _userManager = userManager;
        _accountConfirmationEmailSender = accountConfirmationEmailSender;
        _logger = logger;
    }

    public object? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            EmailConfirmationRequested? message = JsonSerializer.Deserialize<EmailConfirmationRequested>(body);
            return message is null || message.IdentityUserId == Guid.Empty ? null : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<Result> ProcessAsync(object message, CancellationToken cancellationToken)
    {
        return ProcessAsync((EmailConfirmationRequested)message, cancellationToken);
    }

    /// <summary>
    /// Handles one consumed message end-to-end and returns the acknowledgement decision.
    /// </summary>
    /// <returns>
    /// NOT a business outcome — the answer to "does this message need redelivery?". Success means
    /// "ack, drop it from the queue": either the e-mail went out, or redelivery can never change
    /// the outcome (user vanished, address already confirmed, no address on the account) — nacking
    /// those would loop the same message forever. Failure means "worth retrying" (e.g. the SMTP
    /// relay is down) and drives the consumer's nack + requeue.
    /// </returns>
    public async Task<Result> ProcessAsync(EmailConfirmationRequested message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationUser? user = await _userManager.FindByIdAsync(message.IdentityUserId.ToString());
        if (user is null)
        {
            LogUserGone(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (user.EmailConfirmed)
        {
            LogAlreadyConfirmed(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            LogEmailMissing(_logger, message.IdentityUserId);
            return Result.Success();
        }

        string confirmationToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);

        Result sendEmailConfirmationResult = await _accountConfirmationEmailSender.SendEmailConfirmationAsync(
            user.Id,
            user.Email,
            confirmationToken,
            cancellationToken);
        return sendEmailConfirmationResult;
    }

    [LoggerMessage(
        EventId = EventIds.EmailConfirmationUserGone,
        Level = LogLevel.Information,
        Message = "Skipping confirmation e-mail for user {UserId}: the account no longer exists")]
    private static partial void LogUserGone(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.EmailConfirmationAlreadyConfirmed,
        Level = LogLevel.Debug,
        Message = "Skipping confirmation e-mail for user {UserId}: the address is already confirmed")]
    private static partial void LogAlreadyConfirmed(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.EmailConfirmationAddressMissing,
        Level = LogLevel.Warning,
        Message = "Skipping confirmation e-mail for user {UserId}: the account carries no e-mail address")]
    private static partial void LogEmailMissing(ILogger logger, Guid userId);
}
