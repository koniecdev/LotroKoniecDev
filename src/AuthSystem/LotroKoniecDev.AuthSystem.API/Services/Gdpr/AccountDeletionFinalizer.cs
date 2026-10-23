using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// Safe to run twice and to restart: an anonymized address is skipped, one user's failure does not hold
/// back the others and is retried next run, and of two overlapping runs the second loses on the
/// concurrency stamp. <see cref="IAccountDeletionSchedule"/> decides when an account is due, so the date
/// matches what the user was promised (#685).
/// </summary>
internal sealed partial class AccountDeletionFinalizer : IAccountDeletionFinalizer
{
    private readonly AuthDbContext _dbContext;
    private readonly IAccountErasureService _accountErasureService;
    private readonly IAccountDeletionSchedule _deletionSchedule;
    private readonly IErasedAccountReconciler _erasedAccountReconciler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AccountDeletionFinalizer> _logger;

    public AccountDeletionFinalizer(
        AuthDbContext dbContext,
        IAccountErasureService accountErasureService,
        IAccountDeletionSchedule deletionSchedule,
        IErasedAccountReconciler erasedAccountReconciler,
        TimeProvider timeProvider,
        ILogger<AccountDeletionFinalizer> logger)
    {
        _dbContext = dbContext;
        _accountErasureService = accountErasureService;
        _deletionSchedule = deletionSchedule;
        _erasedAccountReconciler = erasedAccountReconciler;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> FinalizeDueAccountsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Oldest first, in the same order every run.
        List<Guid> dueUserIds = await DueUsers(now)
            .OrderBy(u => u.DeletionScheduledAt)
            .ThenBy(u => u.Id)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        int finalizedCount = 0;

        foreach (Guid userId in dueUserIds)
        {
            // A failed save leaves its changes tracked, and one broken account would then stop every
            // erasure after it (#937).
            _dbContext.ChangeTracker.Clear();

            // Read again with the same rule: the owner may have cancelled, or another run erased it.
            ApplicationUser? user;
            try
            {
                user = await DueUsers(now).SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogReadFailedForUser(_logger, ex, userId);
                continue;
            }

            if (user is null)
            {
                continue;
            }

            Result<AccountErasureOutcome> erasureResult = await _accountErasureService.EraseAsync(user);

            if (erasureResult.IsFailure)
            {
                LogFinalizationFailedForUser(_logger, user.Id, erasureResult.Error.Message);
                continue;
            }

            // The erasure logged why, and nothing is left to retry (#962).
            if (erasureResult.Value is AccountErasureOutcome.NoLongerWaiting)
            {
                continue;
            }

            LogDeletionFinalized(_logger, user.Id);
            finalizedCount++;
        }

        await ReconcileErasedAccountsAsync(cancellationToken);

        return finalizedCount;
    }

    /// <summary>
    /// After the erasures, so it covers this run's too. A failure or a shutdown only waits for the next
    /// run, which goes over every erased account again (ADR-0065). A contract with no route is a
    /// programmer error and is never caught here (ADR-0038).
    /// </summary>
    private async Task ReconcileErasedAccountsAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // A failed erasure can leave its AccountErased message tracked, and the announcement saves here.
        _dbContext.ChangeTracker.Clear();

        try
        {
            int scrubbed = await _erasedAccountReconciler.ScrubSentMessagesAsync(cancellationToken);
            if (scrubbed > 0)
            {
                LogErasedAccountMessagesScrubbed(_logger, scrubbed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is not UnroutableOutboxMessageTypeException)
        {
            LogErasedAccountMessagesScrubFailed(_logger, ex);
        }

        try
        {
            int announced = await _erasedAccountReconciler.AnnounceUnannouncedErasuresAsync(cancellationToken);
            if (announced > 0)
            {
                LogErasuresAnnounced(_logger, announced);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping. The next run starts it again.
        }
        catch (Exception ex) when (ex is not UnroutableOutboxMessageTypeException)
        {
            LogErasuresAnnounceFailed(_logger, ex);
        }
    }

    private IQueryable<ApplicationUser> DueUsers(DateTimeOffset now) =>
        _dbContext.Users
            .Where(_deletionSchedule.IsDueBy(now))
            .Where(u => !u.Email!.EndsWith(AnonymizationConstants.EmailDomain));

    [LoggerMessage(EventId = EventIds.GdprDeletionFinalized, Level = LogLevel.Information, Message = "GDPR deletion finalized for user {UserId} after the grace period elapsed")]
    private static partial void LogDeletionFinalized(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprDeletionFinalizerUserFailed, Level = LogLevel.Error, Message = "GDPR deletion finalization failed for user {UserId}: {Error}. Will retry on the next run.")]
    private static partial void LogFinalizationFailedForUser(ILogger logger, Guid userId, string error);

    [LoggerMessage(EventId = EventIds.GdprErasedAccountMessagesScrubbed, Level = LogLevel.Information, Message = "GDPR erasure: cut {MessageCount} sent outbox message(s) of erased accounts down to the account id")]
    private static partial void LogErasedAccountMessagesScrubbed(ILogger logger, int messageCount);

    [LoggerMessage(EventId = EventIds.GdprErasedAccountMessagesScrubFailed, Level = LogLevel.Error, Message = "GDPR erasure: cutting the sent outbox messages of erased accounts failed. Their e-mail addresses stay in the outbox until a later run succeeds.")]
    private static partial void LogErasedAccountMessagesScrubFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = EventIds.GdprErasuresAnnounced, Level = LogLevel.Information, Message = "GDPR erasure: told the TMS about {AccountCount} erased account(s) it had not heard of")]
    private static partial void LogErasuresAnnounced(ILogger logger, int accountCount);

    [LoggerMessage(EventId = EventIds.GdprErasuresAnnounceFailed, Level = LogLevel.Error, Message = "GDPR erasure: telling the TMS about erased accounts failed. Their names stay in the TMS until a later run succeeds.")]
    private static partial void LogErasuresAnnounceFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = EventIds.GdprDeletionFinalizerUserReadFailed, Level = LogLevel.Error, Message = "GDPR deletion finalization could not read user {UserId}. Will retry on the next run.")]
    private static partial void LogReadFailedForUser(ILogger logger, Exception exception, Guid userId);
}
