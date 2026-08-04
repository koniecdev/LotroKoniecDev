using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The business reaction to a consumed <see cref="AccountDeletionCancelled"/> message: load the
/// user and dispatch the courtesy notice — no token to mint, the forced-reset token travels in
/// the cancel endpoint's response (see the payload's remarks). Carries the mirror of the
/// deletion-scheduled drift guard (ADR-0038 decision 2): if deletion is scheduled again by the
/// time this is processed, "your account was kept" is a lie and the message is acknowledged
/// without sending.
/// </summary>
/// <remarks>
/// Delivery is at-least-once (ADR-0035), so this must stay idempotent. It is: the e-mail carries
/// no state and no token, so a redelivered message at worst repeats the notice — annoying, never
/// harmful — and every skip branch reads current state, so replays converge on the same decision.
/// </remarks>
internal sealed partial class AccountDeletionCancelledProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAccountDeletionEmailSender _accountDeletionEmailSender;
    private readonly ILogger<AccountDeletionCancelledProcessor> _logger;

    public AccountDeletionCancelledProcessor(
        UserManager<ApplicationUser> userManager,
        IAccountDeletionEmailSender accountDeletionEmailSender,
        ILogger<AccountDeletionCancelledProcessor> logger)
    {
        _userManager = userManager;
        _accountDeletionEmailSender = accountDeletionEmailSender;
        _logger = logger;
    }

    public object? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            AccountDeletionCancelled? message = JsonSerializer.Deserialize<AccountDeletionCancelled>(body);
            return message is null || message.IdentityUserId == Guid.Empty ? null : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<Result> ProcessAsync(object message, CancellationToken cancellationToken)
    {
        return ProcessAsync((AccountDeletionCancelled)message, cancellationToken);
    }

    /// <summary>
    /// Handles one consumed message end-to-end and returns the acknowledgement decision.
    /// </summary>
    /// <returns>
    /// NOT a business outcome — the answer to "does this message need redelivery?". Success means
    /// "ack, drop it from the queue": either the e-mail went out, or redelivery can never change
    /// the outcome (user vanished, deletion scheduled again in the gap, no address on the
    /// account) — nacking those would loop the same message forever. Failure means "worth
    /// retrying" (e.g. the SMTP relay is down) and drives the consumer's reject + requeue.
    /// </returns>
    public async Task<Result> ProcessAsync(AccountDeletionCancelled message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationUser? user = await _userManager.FindByIdAsync(message.IdentityUserId.ToString());
        if (user is null)
        {
            LogUserGone(_logger, message.IdentityUserId);
            return Result.Success();
        }

        // The mirror drift guard — also what stops a post-finalization DLQ replay: erasure keeps
        // DeletionScheduledAt set as its audit trace, so an anonymized account lands here too.
        if (user.DeletionScheduledAt is not null)
        {
            LogDeletionRescheduled(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            LogEmailMissing(_logger, message.IdentityUserId);
            return Result.Success();
        }

        Result sendDeletionCancelledResult = await _accountDeletionEmailSender.SendDeletionCancelledEmailAsync(
            user.Id,
            user.Email,
            cancellationToken);
        return sendDeletionCancelledResult;
    }

    [LoggerMessage(
        EventId = EventIds.DeletionCancelledUserGone,
        Level = LogLevel.Information,
        Message = "Skipping deletion-cancelled e-mail for user {UserId}: the account no longer exists")]
    private static partial void LogUserGone(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.DeletionCancelledRescheduled,
        Level = LogLevel.Information,
        Message = "Skipping deletion-cancelled e-mail for user {UserId}: deletion is scheduled again")]
    private static partial void LogDeletionRescheduled(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.DeletionCancelledAddressMissing,
        Level = LogLevel.Warning,
        Message = "Skipping deletion-cancelled e-mail for user {UserId}: the account carries no e-mail address")]
    private static partial void LogEmailMissing(ILogger logger, Guid userId);
}
