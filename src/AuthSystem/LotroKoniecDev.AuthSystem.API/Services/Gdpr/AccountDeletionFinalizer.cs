using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// Finds accounts whose deletion grace period is over and erases them.
/// It is safe to run twice and safe to restart: accounts that are already anonymized are recognised by
/// the marker in their e-mail address, a failure on one user is logged and retried on the next run,
/// and if two runs overlap the second one simply loses on the Identity concurrency stamp.
/// When an account is due is <see cref="IAccountDeletionSchedule"/>'s call, not this class's: the
/// date it erases on has to be the one the response header, the e-mail and the login page promised
/// (#685).
/// </summary>
internal sealed partial class AccountDeletionFinalizer : IAccountDeletionFinalizer
{
    private readonly AuthDbContext _dbContext;
    private readonly IAccountErasureService _accountErasureService;
    private readonly IAccountDeletionSchedule _deletionSchedule;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AccountDeletionFinalizer> _logger;

    public AccountDeletionFinalizer(
        AuthDbContext dbContext,
        IAccountErasureService accountErasureService,
        IAccountDeletionSchedule deletionSchedule,
        TimeProvider timeProvider,
        ILogger<AccountDeletionFinalizer> logger)
    {
        _dbContext = dbContext;
        _accountErasureService = accountErasureService;
        _deletionSchedule = deletionSchedule;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> FinalizeDueAccountsAsync(CancellationToken cancellationToken)
    {
        List<ApplicationUser> dueUsers = await _dbContext.Users
            .Where(_deletionSchedule.IsDueBy(_timeProvider.GetUtcNow()))
            .Where(u => !u.Email!.EndsWith(AnonymizationConstants.EmailDomain))
            .ToListAsync(cancellationToken);

        int finalizedCount = 0;

        foreach (ApplicationUser user in dueUsers)
        {
            Result erasureResult = await _accountErasureService.EraseAsync(user, cancellationToken);

            if (erasureResult.IsFailure)
            {
                LogFinalizationFailedForUser(_logger, user.Id, erasureResult.Error.Message);
                continue;
            }

            LogDeletionFinalized(_logger, user.Id);
            finalizedCount++;
        }

        return finalizedCount;
    }

    [LoggerMessage(EventId = EventIds.GdprDeletionFinalized, Level = LogLevel.Information, Message = "GDPR deletion finalized for user {UserId} after the grace period elapsed")]
    private static partial void LogDeletionFinalized(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprDeletionFinalizerUserFailed, Level = LogLevel.Error, Message = "GDPR deletion finalization failed for user {UserId}: {Error}. Will retry on the next run.")]
    private static partial void LogFinalizationFailedForUser(ILogger logger, Guid userId, string error);
}
