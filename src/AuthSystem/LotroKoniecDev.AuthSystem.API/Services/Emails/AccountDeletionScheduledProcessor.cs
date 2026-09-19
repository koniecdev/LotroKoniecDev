using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// What happens when an <see cref="AccountDeletionScheduled"/> message arrives: load the user, work
/// out the deletion date again, create the cancel token now rather than earlier (see the payload's
/// remarks about token lifetime), and send the e-mail with the cancel link.
/// The check lives here (ADR-0038 decision 2): if a cancellation arrives at the same time, it wins. An
/// out-of-date "your account will be deleted" must never go out after the schedule is gone.
/// While an undo of ADR-0048 is armed and still live, the same link also goes to the address that undo
/// would restore (#685). After an e-mail change the current address may belong to whoever took the
/// account, and a cancel link only they can read is no protection at all.
/// </summary>
/// <remarks>
/// A message may arrive more than once (ADR-0035), so this has to be safe to run twice. It is: at
/// worst the e-mail is sent again with a new, equally valid cancel token, which is annoying but
/// harmless, and every skip case reads the current state, so a repeat reaches the same decision.
/// </remarks>
internal sealed partial class AccountDeletionScheduledProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAccountDeletionEmailSender _accountDeletionEmailSender;
    private readonly IAccountDeletionSchedule _deletionSchedule;
    private readonly IEmailChangeRevertWindow _revertWindow;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AccountDeletionScheduledProcessor> _logger;

    public AccountDeletionScheduledProcessor(
        UserManager<ApplicationUser> userManager,
        IAccountDeletionEmailSender accountDeletionEmailSender,
        IAccountDeletionSchedule deletionSchedule,
        IEmailChangeRevertWindow revertWindow,
        TimeProvider timeProvider,
        ILogger<AccountDeletionScheduledProcessor> logger)
    {
        _userManager = userManager;
        _accountDeletionEmailSender = accountDeletionEmailSender;
        _deletionSchedule = deletionSchedule;
        _revertWindow = revertWindow;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public object? TryDeserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            AccountDeletionScheduled? message = JsonSerializer.Deserialize<AccountDeletionScheduled>(body);
            return message is null || message.IdentityUserId == Guid.Empty ? null : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<Result> ProcessAsync(object message, CancellationToken cancellationToken)
    {
        return ProcessAsync((AccountDeletionScheduled)message, cancellationToken);
    }

    /// <summary>
    /// Handles one message from start to finish and says whether it may be acknowledged.
    /// </summary>
    /// <returns>
    /// This is not a business result. It answers one question: does this message need to be sent
    /// again? Success means "acknowledge it and drop it from the queue", either because the e-mail went
    /// out or because sending again could never change anything: the user is gone, the deletion was
    /// cancelled in the meantime, the grace period is already over, or the account has no address.
    /// Refusing those would repeat the same message forever. Failure means "worth another try", for
    /// example when the SMTP relay is down, and the consumer then rejects and requeues it.
    /// </returns>
    public async Task<Result> ProcessAsync(AccountDeletionScheduled message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationUser? user = await _userManager.FindByIdAsync(message.IdentityUserId.ToString());
        if (user is null)
        {
            LogUserGone(_logger, message.IdentityUserId);
            return Result.Success();
        }

        if (user.DeletionScheduledAt is null)
        {
            LogScheduleGone(_logger, message.IdentityUserId);
            return Result.Success();
        }

        // Guards against a delivery that arrives much later, such as a replay from the dead-letter
        // queue. Once the cancel token's own window is over, the e-mail's "cancel until <date>" is
        // wrong and the link in it is dead. Erasure also leaves DeletionScheduledAt set, with a
        // made-up address on the row, so the check above on its own would let a late replay create a
        // working cancel token for an anonymized account.
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset cancellableUntil = _deletionSchedule.CancellableUntil(user.DeletionScheduledAt.Value);
        if (cancellableUntil <= now)
        {
            LogWindowOver(_logger, message.IdentityUserId, cancellableUntil);
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            LogEmailMissing(_logger, message.IdentityUserId);
            return Result.Success();
        }

        // The date in the text is the one the account is really erased on, which an armed undo can
        // push past the cancel window above. Nobody is left without a way out in that tail: it only
        // exists while the undo is live, and following the undo cancels the deletion too.
        DateTimeOffset finalizesAt =
            _deletionSchedule.FinalizesAt(user.DeletionScheduledAt.Value, user.EmailChangeRevertArmedAt);

        string cancelToken = await _userManager.GenerateUserTokenAsync(
            user,
            AccountDeletionCancellationTokenProvider.ProviderName,
            AccountDeletionCancellationTokenProvider.CancelDeletionPurpose);

        // The armed address is tried first, for the reason EmailChangeCompletedProcessor gives: it is
        // the one that can still save the account. It is only ordered first, never allowed to cancel
        // the other send — this link is ADR-0031's only recovery path, and a mailbox that has been
        // abandoned since the address changed would otherwise burn every delivery attempt and leave
        // the account holder with no cancel link at all.
        Result previousAddressResult = Result.Success();

        if (_revertWindow.IsLiveAt(user.EmailChangeRevertArmedAt, now)
            && !string.IsNullOrWhiteSpace(user.EmailChangeRevertTo))
        {
            previousAddressResult =
                await _accountDeletionEmailSender.SendDeletionScheduledNoticeToPreviousAddressAsync(
                    user.Id,
                    user.EmailChangeRevertTo,
                    user.Email,
                    cancelToken,
                    finalizesAt,
                    cancellationToken);

            if (previousAddressResult.IsFailure)
            {
                LogPreviousAddressSendFailed(
                    _logger, message.IdentityUserId, previousAddressResult.Error.Message);
            }
            else
            {
                LogPreviousAddressNotified(_logger, message.IdentityUserId);
            }
        }

        Result currentAddressResult = await _accountDeletionEmailSender.SendDeletionScheduledEmailAsync(
            user.Id,
            user.Email,
            cancelToken,
            finalizesAt,
            cancellationToken);

        // Either failure requeues the message and re-sends both, which is the at-least-once bar of
        // ADR-0038 — the same link arriving twice changes nothing. The current address wins when both
        // failed, because its copy is the one ADR-0031 promises.
        return currentAddressResult.IsFailure ? currentAddressResult : previousAddressResult;
    }

    [LoggerMessage(
        EventId = EventIds.DeletionScheduledUserGone,
        Level = LogLevel.Information,
        Message = "Skipping deletion-scheduled e-mail for user {UserId}: the account no longer exists")]
    private static partial void LogUserGone(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.DeletionScheduledScheduleGone,
        Level = LogLevel.Information,
        Message = "Skipping deletion-scheduled e-mail for user {UserId}: the deletion is no longer scheduled")]
    private static partial void LogScheduleGone(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.DeletionScheduledWindowOver,
        Level = LogLevel.Warning,
        Message = "Skipping deletion-scheduled e-mail for user {UserId}: the cancel window ended at {CancellableUntil}")]
    private static partial void LogWindowOver(ILogger logger, Guid userId, DateTimeOffset cancellableUntil);

    [LoggerMessage(
        EventId = EventIds.DeletionScheduledPreviousAddressNotified,
        Level = LogLevel.Information,
        Message = "Deletion-scheduled e-mail for user {UserId} also went to the address an armed undo would restore")]
    private static partial void LogPreviousAddressNotified(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = EventIds.DeletionScheduledPreviousAddressFailed,
        Level = LogLevel.Warning,
        Message = "Could not notify the armed undo address for user {UserId}: {Error}. The current address is still served.")]
    private static partial void LogPreviousAddressSendFailed(ILogger logger, Guid userId, string error);

    [LoggerMessage(
        EventId = EventIds.DeletionScheduledAddressMissing,
        Level = LogLevel.Warning,
        Message = "Skipping deletion-scheduled e-mail for user {UserId}: the account carries no e-mail address")]
    private static partial void LogEmailMissing(ILogger logger, Guid userId);
}
