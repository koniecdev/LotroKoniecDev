using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

/// <summary>
/// The business reaction to a consumed <see cref="AccountDeletionScheduled"/> message: load the
/// user, recompute the deletion date, mint the cancel token at send time (see the payload's
/// remarks on token lifetime), and dispatch the e-mail with the cancel link. The drift guard
/// lives here (ADR-0038 decision 2): a cancellation racing this message wins — a stale "your
/// account will be deleted" must never go out after the schedule is gone.
/// </summary>
/// <remarks>
/// Delivery is at-least-once (ADR-0035), so this must stay idempotent. It is: a redelivered
/// message at worst re-sends the e-mail with a fresh, equally valid cancel token — annoying,
/// never harmful — and every skip branch reads current state, so replays converge on the same
/// decision.
/// </remarks>
internal sealed partial class AccountDeletionScheduledProcessor : IEmailMessageProcessor
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAccountDeletionEmailSender _accountDeletionEmailSender;
    private readonly TimeProvider _timeProvider;
    private readonly GdprSettings _gdprSettings;
    private readonly ILogger<AccountDeletionScheduledProcessor> _logger;

    public AccountDeletionScheduledProcessor(
        UserManager<ApplicationUser> userManager,
        IAccountDeletionEmailSender accountDeletionEmailSender,
        TimeProvider timeProvider,
        IOptions<GdprSettings> gdprSettings,
        ILogger<AccountDeletionScheduledProcessor> logger)
    {
        _userManager = userManager;
        _accountDeletionEmailSender = accountDeletionEmailSender;
        _timeProvider = timeProvider;
        _gdprSettings = gdprSettings.Value;
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
    /// Handles one consumed message end-to-end and returns the acknowledgement decision.
    /// </summary>
    /// <returns>
    /// NOT a business outcome — the answer to "does this message need redelivery?". Success means
    /// "ack, drop it from the queue": either the e-mail went out, or redelivery can never change
    /// the outcome (user vanished, schedule cancelled in the gap, deletion window already over,
    /// no address on the account) — nacking those would loop the same message forever. Failure
    /// means "worth retrying" (e.g. the SMTP relay is down) and drives the consumer's reject +
    /// requeue.
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

        // Guards against a much-delayed delivery (a DLQ replay long after the fact): once the
        // grace window is over the e-mail's "cancel until <date>" is a lie — and erasure keeps
        // DeletionScheduledAt as its audit trace with a synthetic address on the row, so the
        // schedule-gone guard above alone would let a post-finalization replay mint a working
        // cancel token for an anonymized account.
        DateTimeOffset finalizesAt = user.DeletionScheduledAt.Value + _gdprSettings.DeletionGracePeriod;
        if (finalizesAt <= _timeProvider.GetUtcNow())
        {
            LogWindowOver(_logger, message.IdentityUserId, finalizesAt);
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            LogEmailMissing(_logger, message.IdentityUserId);
            return Result.Success();
        }

        string cancelToken = await _userManager.GenerateUserTokenAsync(
            user,
            AccountDeletionCancellationTokenProvider.ProviderName,
            AccountDeletionCancellationTokenProvider.CancelDeletionPurpose);

        Result sendDeletionScheduledResult = await _accountDeletionEmailSender.SendDeletionScheduledEmailAsync(
            user.Id,
            user.Email,
            cancelToken,
            finalizesAt,
            cancellationToken);
        return sendDeletionScheduledResult;
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
        Message = "Skipping deletion-scheduled e-mail for user {UserId}: the grace window ended at {FinalizesAt}")]
    private static partial void LogWindowOver(ILogger logger, Guid userId, DateTimeOffset finalizesAt);

    [LoggerMessage(
        EventId = EventIds.DeletionScheduledAddressMissing,
        Level = LogLevel.Warning,
        Message = "Skipping deletion-scheduled e-mail for user {UserId}: the account carries no e-mail address")]
    private static partial void LogEmailMissing(ILogger logger, Guid userId);
}
