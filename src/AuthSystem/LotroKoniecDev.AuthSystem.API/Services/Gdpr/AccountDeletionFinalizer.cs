using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// Finds accounts whose deletion grace period is over and erases them.
/// It is safe to run twice and safe to restart: accounts that are already anonymized are recognised by
/// the marker in their e-mail address, a failure on one user is logged, does not hold back the others
/// and is retried on the next run, and if two runs overlap the second one loses on the Identity
/// concurrency stamp and leaves the account to the first.
/// When an account is due is <see cref="IAccountDeletionSchedule"/>'s call, not this class's: the
/// date it erases on has to be the one the response header, the e-mail and the login page promised
/// (#685).
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

        // The oldest schedule goes first, and every run takes the accounts in the same order.
        List<Guid> dueUserIds = await DueUsers(now)
            .OrderBy(u => u.DeletionScheduledAt)
            .ThenBy(u => u.Id)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        int finalizedCount = 0;

        foreach (Guid userId in dueUserIds)
        {
            // Every account starts on an empty change tracker. A failed save leaves the account's
            // changes tracked, and the next save in this context would write them again and fail with
            // them, so one broken account would stop every erasure after it (#937).
            _dbContext.ChangeTracker.Clear();

            // Read again, with the rule that listed it: the owner may have cancelled the deletion, or
            // another run may have erased the account, since the list was read.
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

            // The erasure has logged why. Nothing is left to retry, so this line must not promise it (#962).
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
    /// Runs after the erasures, so it also covers the accounts this run erased. A failure only waits
    /// for the next run, which goes over every erased account again (ADR-0065).
    /// </summary>
    private async Task ReconcileErasedAccountsAsync(CancellationToken cancellationToken)
    {
        // A failed erasure can leave its AccountErased message tracked, and the reconciler saves through
        // this context. That message must never be saved for an account that is not erased.
        _dbContext.ChangeTracker.Clear();

        try
        {
            ErasedAccountReconciliation reconciliation = await _erasedAccountReconciler.ReconcileAsync(cancellationToken);
            if (reconciliation.MessagesScrubbed > 0 || reconciliation.ErasuresAnnounced > 0)
            {
                LogErasedAccountsReconciled(_logger, reconciliation.MessagesScrubbed, reconciliation.ErasuresAnnounced);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogErasedAccountsReconcileFailed(_logger, ex);
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

    [LoggerMessage(EventId = EventIds.GdprErasedAccountsReconciled, Level = LogLevel.Information, Message = "GDPR erasure: cut {MessageCount} sent outbox message(s) of erased accounts down to the account id, and told the TMS about {AnnouncedCount} erased account(s) it had not heard of")]
    private static partial void LogErasedAccountsReconciled(ILogger logger, int messageCount, int announcedCount);

    [LoggerMessage(EventId = EventIds.GdprErasedAccountsReconcileFailed, Level = LogLevel.Error, Message = "GDPR erasure: bringing the outbox in line with the erased accounts failed. Their e-mail addresses stay in the outbox, or the TMS keeps their names, until the next run succeeds.")]
    private static partial void LogErasedAccountsReconcileFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = EventIds.GdprDeletionFinalizerUserReadFailed, Level = LogLevel.Error, Message = "GDPR deletion finalization could not read user {UserId}. Will retry on the next run.")]
    private static partial void LogReadFailedForUser(ILogger logger, Exception exception, Guid userId);
}
