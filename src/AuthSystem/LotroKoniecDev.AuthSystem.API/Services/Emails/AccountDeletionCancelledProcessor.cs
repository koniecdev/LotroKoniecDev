using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// What happens when an <see cref="AccountDeletionCancelled"/> message arrives: load the user and send
/// the notice. There is no token to create here, because the reset token travels in the cancel
/// endpoint's response (see the payload's remarks).
/// It carries the mirror of the deletion-scheduled check (ADR-0038 decision 2): if a deletion has been
/// scheduled again by the time this runs, "your account was kept" would be wrong, so the message is
/// acknowledged without sending anything.
/// </summary>
/// <remarks>
/// A message may arrive more than once (ADR-0035), so this has to be safe to run twice. It is: the
/// e-mail carries no state and no token, so at worst the user gets the notice again, which is annoying
/// but harmless, and every skip case reads the current state, so a repeat reaches the same decision.
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
    /// Handles one message from start to finish and says whether it may be acknowledged.
    /// </summary>
    /// <returns>
    /// This is not a business result. It answers one question: does this message need to be sent
    /// again? Success means "acknowledge it and drop it from the queue", either because the e-mail went
    /// out or because sending again could never change anything: the user is gone, a deletion was
    /// scheduled again in the meantime, or the account has no address. Refusing those would repeat the
    /// same message forever. Failure means "worth another try", for example when the SMTP relay is
    /// down, and the consumer then rejects and requeues it.
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

        // The mirror check. It also stops a replay from the dead-letter queue after the deletion
        // finished: erasure leaves DeletionScheduledAt set as its record, so an anonymized account
        // ends up here as well.
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
