using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The business reaction to a consumed <see cref="PasswordResetRequested"/> message: load the
/// user, mint the reset token at send time (see the payload's remarks on token lifetime), and
/// dispatch the e-mail. Also the single home of the deletion-window guard (ADR-0038 decision 2):
/// while GDPR deletion is scheduled, the emailed cancel-deletion link is the only recovery path —
/// a password reset would neither unlock the account nor stop the deletion, so the message is
/// acknowledged without sending.
/// </summary>
/// <remarks>
/// Delivery is at-least-once (ADR-0035), so this must stay idempotent. It does: a redelivered
/// message at worst re-sends a reset e-mail with a fresh token — annoying, never harmful — and
/// every skip branch reads current state, so replays converge on the same decision.
/// </remarks>
internal sealed partial class PasswordResetRequestProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPasswordResetEmailSender _passwordResetEmailSender;
    private readonly ILogger<PasswordResetRequestProcessor> _logger;

    public PasswordResetRequestProcessor(
        UserManager<ApplicationUser> userManager,
        IPasswordResetEmailSender passwordResetEmailSender,
        ILogger<PasswordResetRequestProcessor> logger)
    {
        _userManager = userManager;
        _passwordResetEmailSender = passwordResetEmailSender;
        _logger = logger;
    }

    public object? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            PasswordResetRequested? message = JsonSerializer.Deserialize<PasswordResetRequested>(body);
            return message is null || message.IdentityUserId == Guid.Empty ? null : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<Result> ProcessAsync(object message, CancellationToken cancellationToken)
    {
        return ProcessAsync((PasswordResetRequested)message, cancellationToken);
    }

    /// <summary>
    /// Handles one consumed message end-to-end and returns the acknowledgement decision.
    /// </summary>
    /// <returns>
    /// NOT a business outcome — the answer to "does this message need redelivery?". Success means
    /// "ack, drop it from the queue": either the e-mail went out, or redelivery can never change
    /// the outcome (user vanished, deletion scheduled, no address on the account) — nacking those
    /// would loop the same message forever. Failure means "worth retrying" (e.g. the SMTP relay
    /// is down) and drives the consumer's reject + requeue.
    /// </returns>
    public async Task<Result> ProcessAsync(PasswordResetRequested message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationUser? user = await _userManager.FindByIdAsync(message.IdentityUserId.ToString());
        if (user is null)
        {
            LogUserGone(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (user.DeletionScheduledAt is not null)
        {
            LogDeletionScheduled(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            LogEmailMissing(_logger, message.IdentityUserId);
            return Result.Success();
        }

        string resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);

        Result sendPasswordResetResult = await _passwordResetEmailSender.SendPasswordResetEmailAsync(
            user.Id,
            user.Email,
            resetToken,
            cancellationToken);
        return sendPasswordResetResult;
    }

    [LoggerMessage(
        EventId = EventIds.PasswordResetUserGone,
        Level = LogLevel.Information,
        Message = "Skipping password-reset e-mail for user {UserId}: the account no longer exists")]
    private static partial void LogUserGone(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.PasswordResetDeletionScheduled,
        Level = LogLevel.Information,
        Message = "Skipping password-reset e-mail for user {UserId}: account deletion is scheduled")]
    private static partial void LogDeletionScheduled(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.PasswordResetAddressMissing,
        Level = LogLevel.Warning,
        Message = "Skipping password-reset e-mail for user {UserId}: the account carries no e-mail address")]
    private static partial void LogEmailMissing(ILogger logger, Guid userId);
}
