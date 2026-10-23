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
/// Creates the cancel token at send time and checks the schedule here, so a cancellation that arrives
/// at the same time wins (ADR-0038 decision 2). While an undo is live, the link also goes to the address
/// the undo would restore (#685): after an e-mail change the current address may belong to an attacker.
/// </summary>
/// <remarks>
/// Safe to run twice: at worst the e-mail goes again with a new, equally valid token, and every skip
/// case reads the current state.
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

    /// <returns>
    /// Success also when the user is gone, the deletion was cancelled, the cancel window is over or the
    /// account has no address (see <see cref="IEmailMessageProcessor.ProcessAsync"/>).
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

        // Against a late delivery, such as a dead-letter replay: the cancel endpoint refuses the link
        // after the window anyway (#947). Erasure also leaves DeletionScheduledAt set, so the check above
        // alone would mint a working cancel token for an anonymized account.
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!_deletionSchedule.IsCancellableAt(user.DeletionScheduledAt.Value, now))
        {
            LogWindowOver(
                _logger,
                message.IdentityUserId,
                _deletionSchedule.CancellableUntil(user.DeletionScheduledAt.Value));
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            LogEmailMissing(_logger, message.IdentityUserId);
            return Result.Success();
        }

        // The real erasure date, which an armed undo can push past the cancel window. That tail exists
        // only while the undo is live, and the undo cancels the deletion too.
        DateTimeOffset finalizesAt =
            _deletionSchedule.FinalizesAt(user.DeletionScheduledAt.Value, user.EmailChangeRevertArmedAt);

        string cancelToken = await _userManager.GenerateUserTokenAsync(
            user,
            AccountDeletionCancellationTokenProvider.ProviderName,
            AccountDeletionCancellationTokenProvider.CancelDeletionPurpose);

        // The armed address goes first, because it can still save the account, but its failure never
        // stops the other send: an abandoned mailbox must not leave the holder without a cancel link.
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

        // Either failure requeues and re-sends both (ADR-0038). When both failed, the current address's
        // error wins, because ADR-0031 promises that copy.
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
