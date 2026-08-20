using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// What happens when a <see cref="PasswordResetRequested"/> message arrives: load the user, create the
/// reset token now rather than earlier (see the payload's remarks about token lifetime), and send the
/// e-mail.
/// This is also the one place that checks the deletion window (ADR-0038 decision 2). While a GDPR
/// deletion is scheduled, the cancel link in the e-mail is the only way back, because a password reset
/// would neither unlock the account nor stop the deletion. So the message is acknowledged without
/// sending anything.
/// </summary>
/// <remarks>
/// A message may arrive more than once (ADR-0035), so this has to be safe to run twice. It is: at
/// worst a reset e-mail is sent again with a new token, which is annoying but harmless, and every skip
/// case reads the current state, so a repeat reaches the same decision.
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
    /// Handles one message from start to finish and says whether it may be acknowledged.
    /// </summary>
    /// <returns>
    /// This is not a business result. It answers one question: does this message need to be sent
    /// again? Success means "acknowledge it and drop it from the queue", either because the e-mail went
    /// out or because sending again could never change anything: the user is gone, a deletion is
    /// scheduled, or the account has no address. Refusing those would repeat the same message forever.
    /// Failure means "worth another try", for example when the SMTP relay is down, and the consumer
    /// then rejects and requeues it.
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
