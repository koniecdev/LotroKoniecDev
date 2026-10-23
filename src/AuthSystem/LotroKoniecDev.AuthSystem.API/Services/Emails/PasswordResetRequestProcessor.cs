using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// Creates the reset token at send time. The one place that checks the deletion window (ADR-0038
/// decision 2): during a scheduled deletion a reset would neither unlock the account nor stop the
/// deletion, so nothing is sent.
/// </summary>
/// <remarks>
/// Safe to run twice: at worst the e-mail goes again with a new token.
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

    /// <returns>
    /// Success also when the user is gone, a deletion is scheduled or the account has no address (see
    /// <see cref="IEmailMessageProcessor.ProcessAsync"/>).
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
