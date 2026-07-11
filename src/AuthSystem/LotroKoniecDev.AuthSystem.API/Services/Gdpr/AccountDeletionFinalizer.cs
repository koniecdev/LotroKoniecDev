using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// Finds accounts whose deletion grace period has elapsed and erases them.
/// Idempotent and restart-safe: already-anonymized accounts are filtered out by the
/// anonymization email marker, per-user failures are logged and retried on the next run,
/// and concurrent runs lose harmlessly on the Identity concurrency stamp.
/// </summary>
internal sealed partial class AccountDeletionFinalizer : IAccountDeletionFinalizer
{
    private readonly AuthDbContext _dbContext;
    private readonly IAccountErasureService _accountErasureService;
    private readonly TimeProvider _timeProvider;
    private readonly GdprSettings _gdprSettings;
    private readonly ILogger<AccountDeletionFinalizer> _logger;

    public AccountDeletionFinalizer(
        AuthDbContext dbContext,
        IAccountErasureService accountErasureService,
        TimeProvider timeProvider,
        IOptions<GdprSettings> gdprSettings,
        ILogger<AccountDeletionFinalizer> logger)
    {
        _dbContext = dbContext;
        _accountErasureService = accountErasureService;
        _timeProvider = timeProvider;
        _gdprSettings = gdprSettings.Value;
        _logger = logger;
    }

    public async Task<int> FinalizeDueAccountsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset dueBefore = _timeProvider.GetUtcNow() - _gdprSettings.DeletionGracePeriod;

        List<ApplicationUser> dueUsers = await _dbContext.Users
            .Where(u => u.DeletionScheduledAt != null
                        && u.DeletionScheduledAt <= dueBefore
                        && !u.Email!.EndsWith(AnonymizationConstants.EmailDomain))
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
