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
/// What happens when an <see cref="AccountDeletionScheduled"/> message arrives: load the user, work
/// out the deletion date again, create the cancel token now rather than earlier (see the payload's
/// remarks about token lifetime), and send the e-mail with the cancel link.
/// The check lives here (ADR-0038 decision 2): if a cancellation arrives at the same time, it wins. An
/// out-of-date "your account will be deleted" must never go out after the schedule is gone.
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
        // queue. Once the grace period is over, the e-mail's "cancel until <date>" is wrong. Erasure
        // also leaves DeletionScheduledAt set, with a made-up address on the row, so the check above
        // on its own would let a late replay create a working cancel token for an anonymized
        // account.
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
