using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The business reaction to a consumed <see cref="EmailConfirmationRequested"/> message: load the
/// user, mint the confirmation token at send time (see the payload's remarks on token lifetime),
/// and dispatch the e-mail. Success means "this message needs no redelivery" — a vanished or
/// already-confirmed user is a success, because retrying can never change the outcome. Failure
/// means "worth retrying" and drives the consumer's nack.
/// </summary>
/// <remarks>
/// Delivery is at-least-once (ADR-0035), so this must stay idempotent. Today that comes free:
/// a redelivered message finds <see cref="ApplicationUser.EmailConfirmed"/> already set, or at
/// worst re-sends a confirmation e-mail — annoying, never harmful. A new message type must
/// re-earn this property before it may reuse the pattern.
/// </remarks>
internal sealed partial class EmailConfirmationRequestProcessor
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

    public async Task<Result> ProcessAsync(EmailConfirmationRequested message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationUser? user = await _userManager.FindByIdAsync(message.UserId.ToString());
        if (user is null)
        {
            LogUserGone(_logger, message.UserId);
            return Result.Success();
        }

        if (user.EmailConfirmed)
        {
            LogAlreadyConfirmed(_logger, message.UserId);
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            LogAddressMissing(_logger, message.UserId);
            return Result.Success();
        }

        string confirmationToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);

        return await _accountConfirmationEmailSender.SendEmailConfirmationAsync(
            user.Id,
            user.Email,
            confirmationToken,
            cancellationToken);
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
    private static partial void LogAddressMissing(ILogger logger, Guid userId);
}
