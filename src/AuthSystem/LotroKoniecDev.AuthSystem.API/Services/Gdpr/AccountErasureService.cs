using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.IntegrationEvents;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// The part of a deletion that cannot be undone: anonymize, lock for good, clean up (ADR-0031). The
/// anonymizing save also writes an <see cref="AccountErased"/> message, so the TMS erases its own copy
/// of the name and address (ADR-0065). Safe to run twice: callers skip an already anonymized address.
/// </summary>
internal sealed partial class AccountErasureService : IAccountErasureService
{
    /// <summary>
    /// No full stop at the end: the finalizer's log line adds its own sentence after it.
    /// </summary>
    private const string AnonymizationFailedDetails = "The account could not be anonymized";

    private readonly AuthDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IOpenIddictTokenManager _tokenManager;
    private readonly IOpenIddictAuthorizationManager _authorizationManager;
    private readonly OutboxWriter _outboxWriter;
    private readonly ILogger<AccountErasureService> _logger;

    public AccountErasureService(
        AuthDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        OutboxWriter outboxWriter,
        ILogger<AccountErasureService> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _tokenManager = tokenManager;
        _authorizationManager = authorizationManager;
        _outboxWriter = outboxWriter;
        _logger = logger;
    }

    public async Task<Result<AccountErasureOutcome>> EraseAsync(ApplicationUser user)
    {
        LogGdprErasureInitiated(_logger, user.Id);

        string anonymizedGuid = Guid.NewGuid().ToString("N");
        string anonymizedEmail = $"{AnonymizationConstants.EmailPrefix}{anonymizedGuid}{AnonymizationConstants.EmailDomain}";
        FailedSave? failedSave = null;

        // Before this run changes the account: when the check after a failed lock fails too, this is all
        // the run knows about an earlier run's lock (#1018).
        bool lockedForGoodWhenRead = user.LockoutEnabled && user.LockoutEnd == DateTimeOffset.MaxValue;

        // Commits in the anonymizing save: no run comes back after it, so a message written later and
        // lost would leave the name in the TMS for good (ADR-0065). Outside the try, because a type with
        // no route is a programmer error that must fail loudly.
        _outboxWriter.Enqueue(new AccountErased(user.Id));

        try
        {
            // A stored sign-in session holds the name and e-mail too (ADR-0062). Before the anonymizing
            // save, because no run retries after it. Only while the account still waits: an owner who
            // cancelled meanwhile may have signed in again.
            Guid userId = user.Id;
            await _dbContext.SignInSessions
                .Where(session => session.UserId == userId
                                  && _dbContext.Users.Any(u => u.Id == userId
                                                               && u.DeletionScheduledAt != null
                                                               && !u.Email!.EndsWith(AnonymizationConstants.EmailDomain)))
                .ExecuteDeleteAsync(CancellationToken.None);

            // DeletionScheduledAt stays: it is not personal data, and it records when the erasure was asked for.
            user.UserName = anonymizedGuid;
            user.NormalizedUserName = anonymizedGuid.ToUpperInvariant();
            user.Email = anonymizedEmail;
            user.NormalizedEmail = anonymizedEmail.ToUpperInvariant();
            user.PhoneNumber = null;
            user.PasswordHash = null;
            user.EmailConfirmed = false;
            user.PhoneNumberConfirmed = false;
            user.TwoFactorEnabled = false;
            user.AccessFailedCount = 0;
            user.DataProcessingConsentGiven = false;
            user.DataProcessingConsentDate = null;
            user.PrivacyPolicyAccepted = false;
            user.PrivacyPolicyAcceptedDate = null;
            user.TermsOfServiceAccepted = false;
            user.TermsOfServiceAcceptedDate = null;

            // A former address is personal data. Clearing it also releases the reservation (#684), so an
            // erased account does not keep an address blocked.
            user.DisarmEmailChangeRevert();

            // In the same update as the anonymized address: the finalizer picks work by that address, so a
            // separate write after this save that failed would never be retried (#908).
            user.LockoutEnabled = true;
            user.LockoutEnd = DateTimeOffset.MaxValue;
            user.SecurityStamp = Guid.NewGuid().ToString();

            IdentityResult updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                failedSave = new FailedSave(Describe(updateResult), Exception: null);
            }
        }
        catch (Exception ex)
        {
            failedSave = new FailedSave(ex.GetBaseException().Message, ex);
        }

        if (failedSave is not null)
        {
            Result<AccountErasureOutcome> afterFailedSave =
                await ResolveFailedSaveAsync(user.Id, anonymizedEmail, lockedForGoodWhenRead, failedSave);

            if (afterFailedSave.IsFailure || afterFailedSave.Value is AccountErasureOutcome.NoLongerWaiting)
            {
                return afterFailedSave;
            }
        }

        LogAuthDataAnonymized(_logger, user.Id);

        // Best effort: the account is already anonymized and locked.
        await CleanupAuthArtifactsAsync(user.Id);

        LogAccountDeleted(_logger, user.Id);

        return AccountErasureOutcome.Erased;
    }

    /// <summary>
    /// Works out what a failed save means before logging, so the log never promises a retry that will not
    /// come (#962). A landed lock means the next run retries. Otherwise the account is read: this run's own
    /// save may have landed with only the answer lost, or the account no longer waits (cancelled or erased
    /// by another run). A failed lock calls for help only when a lock was still needed, that is when the
    /// account waits and is not yet locked for good, or when the read fails too (#980, #1018).
    /// </summary>
    private async Task<Result<AccountErasureOutcome>> ResolveFailedSaveAsync(
        Guid userId,
        string anonymizedEmail,
        bool lockedForGoodWhenRead,
        FailedSave failedSave)
    {
        (bool locked, Exception? lockFailure) = await TryLockAccountAsync(userId);

        if (locked)
        {
            return FailForTheNextRun(userId, failedSave);
        }

        AccountState state;
        try
        {
            // The address is new for every attempt, so only this run's save can have written it.
            DateTimeOffset? lockedUntil = DateTimeOffset.MaxValue;
            state = await _dbContext.Users
                .Where(u => u.Id == userId)
                .Select(u => new AccountState(
                    u.Email == anonymizedEmail,
                    u.DeletionScheduledAt != null && !u.Email!.EndsWith(AnonymizationConstants.EmailDomain),
                    u.LockoutEnabled && u.LockoutEnd == lockedUntil))
                .SingleOrDefaultAsync(CancellationToken.None)
                ?? new AccountState(SaveLanded: false, StillWaits: false, LockedForGood: false);
        }
        catch (Exception ex)
        {
            if (lockFailure is null)
            {
                LogSaveOutcomeUnknown(_logger, ex, userId, failedSave.Errors);
                return AccountErasureOutcome.NoLongerWaiting;
            }

            // Nothing new is known, so the account is taken to still wait. Only an earlier run's lock seen
            // by the finalizer's read keeps the alert away: the writes that lower that lock also end the
            // schedule (#861, #1018).
            LogSaveCheckFailedAfterFailedLock(_logger, ex, userId);
            state = new AccountState(SaveLanded: false, StillWaits: true, LockedForGood: lockedForGoodWhenRead);
        }

        if (lockFailure is not null)
        {
            if (state.StillWaits)
            {
                if (state.LockedForGood)
                {
                    LogLockoutFailedOnLockedAccount(_logger, lockFailure, userId);
                }
                else
                {
                    LogEmergencyLockoutFailed(_logger, lockFailure, userId);
                }

                return FailForTheNextRun(userId, failedSave);
            }

            LogUnneededLockoutFailed(_logger, lockFailure, userId);
        }

        if (state.SaveLanded)
        {
            // One SaveChanges whose account row landed, so all of it landed. Otherwise the cleanup's next
            // save would write it again against the old concurrency stamp and fail.
            _dbContext.ChangeTracker.AcceptAllChanges();
            LogSaveLandedAfterAll(_logger, failedSave.Exception, userId, failedSave.Errors);
            return AccountErasureOutcome.Erased;
        }

        // A lost concurrency check is a race. An exception is a real database error that the next account
        // may hit too.
        LogLevel level = failedSave.Exception is null ? LogLevel.Information : LogLevel.Warning;
        LogNoLongerWaiting(_logger, level, failedSave.Exception, userId, failedSave.Errors);
        return AccountErasureOutcome.NoLongerWaiting;
    }

    /// <summary>
    /// The account still waits, or may, so the next finalizer run picks it up again.
    /// </summary>
    private Result<AccountErasureOutcome> FailForTheNextRun(Guid userId, FailedSave failedSave)
    {
        if (failedSave.Exception is { } exception)
        {
            LogAuthSideErasureFailed(_logger, exception, userId);
        }
        else
        {
            LogAnonymizationFailed(_logger, userId, failedSave.Errors);
        }

        return Result.Failure<AccountErasureOutcome>(AuthErrors.AccountDeletionFailed(AnonymizationFailedDetails));
    }

    /// <summary>
    /// Every step runs even after a failure, and the log names each failed step. Identity and OpenIddict
    /// report some failures as results, not exceptions, so each result is checked (#962). A failed write
    /// leaves its changes in the shared context, and the next save would fail with them, so every failure
    /// empties the context (#981).
    /// </summary>
    private async Task CleanupAuthArtifactsAsync(Guid userId)
    {
        string subject = userId.ToString();
        (string Step, IAsyncEnumerable<CleanupFailure> Failures)[] steps =
        [
            ("tokens", RevokeTokensAsync(subject)),
            ("authorizations", RevokeAuthorizationsAsync(subject)),
            ("roles", RemoveRolesAsync(userId)),
            ("claims", RemoveClaimsAsync(userId)),
            ("logins", RemoveLoginsAsync(userId))
        ];

        List<(string Step, CleanupFailure Failure)> failed = [];

        foreach ((string step, IAsyncEnumerable<CleanupFailure> failures) in steps)
        {
            try
            {
                await foreach (CleanupFailure failure in failures)
                {
                    failed.Add((step, failure));
                    _dbContext.ChangeTracker.Clear();
                }
            }
            catch (Exception ex)
            {
                failed.Add((step, new CleanupFailure($"{ex.GetBaseException().Message} (the step stopped here)", ex)));
                _dbContext.ChangeTracker.Clear();
            }
        }

        if (failed.Count == 0)
        {
            LogArtifactsCleaned(_logger, userId);
            return;
        }

        List<Exception> exceptions = failed
            .Select(entry => entry.Failure.Exception)
            .OfType<Exception>()
            .ToList();
        Exception? exception = exceptions.Count switch
        {
            0 => null,
            1 => exceptions[0],
            _ => new AggregateException(exceptions)
        };
        LogArtifactsCleanupFailed(
            _logger,
            exception,
            userId,
            string.Join("; ", failed.Select(entry => $"{entry.Step}: {entry.Failure.Reason}")));
    }

    /// <summary>
    /// Skips a revoked token: OpenIddict's token manager writes it again anyway, and if the prune removed
    /// the row meanwhile, that would log a false failure. Scheduling the deletion usually revoked them.
    /// </summary>
    private async IAsyncEnumerable<CleanupFailure> RevokeTokensAsync(string subject)
    {
        await foreach (object token in _tokenManager.FindBySubjectAsync(subject))
        {
            if (await _tokenManager.HasStatusAsync(token, OpenIddictConstants.Statuses.Revoked))
            {
                continue;
            }

            if (!await _tokenManager.TryRevokeAsync(token))
            {
                yield return new CleanupFailure($"{await _tokenManager.GetIdAsync(token)} not revoked", Exception: null);
            }
        }
    }

    private async IAsyncEnumerable<CleanupFailure> RevokeAuthorizationsAsync(string subject)
    {
        await foreach (object authorization in _authorizationManager.FindBySubjectAsync(subject))
        {
            if (!await _authorizationManager.TryRevokeAsync(authorization))
            {
                yield return new CleanupFailure(
                    $"{await _authorizationManager.GetIdAsync(authorization)} not revoked",
                    Exception: null);
            }
        }
    }

    private async IAsyncEnumerable<CleanupFailure> RemoveRolesAsync(Guid userId)
    {
        ApplicationUser account = await FindAccountAsync(userId);
        IList<string> roles = await _userManager.GetRolesAsync(account);
        if (roles.Count > 0 && await _userManager.RemoveFromRolesAsync(account, roles) is { Succeeded: false } result)
        {
            yield return new CleanupFailure(Describe(result), Exception: null);
        }
    }

    private async IAsyncEnumerable<CleanupFailure> RemoveClaimsAsync(Guid userId)
    {
        ApplicationUser account = await FindAccountAsync(userId);
        IList<Claim> claims = await _userManager.GetClaimsAsync(account);
        if (claims.Count > 0 && await _userManager.RemoveClaimsAsync(account, claims) is { Succeeded: false } result)
        {
            yield return new CleanupFailure(Describe(result), Exception: null);
        }
    }

    private async IAsyncEnumerable<CleanupFailure> RemoveLoginsAsync(Guid userId)
    {
        IList<UserLoginInfo> logins = await _userManager.GetLoginsAsync(await FindAccountAsync(userId));
        foreach (UserLoginInfo login in logins)
        {
            if (await TryRemoveLoginAsync(userId, login) is { } failure)
            {
                yield return failure;
            }
        }
    }

    /// <summary>
    /// Catches its own exception, so the logins after a failed one are still removed.
    /// </summary>
    private async Task<CleanupFailure?> TryRemoveLoginAsync(Guid userId, UserLoginInfo login)
    {
        try
        {
            ApplicationUser account = await FindAccountAsync(userId);
            IdentityResult result = await _userManager.RemoveLoginAsync(account, login.LoginProvider, login.ProviderKey);
            return result.Succeeded
                ? null
                : new CleanupFailure($"{login.LoginProvider}: {Describe(result)}", Exception: null);
        }
        catch (Exception ex)
        {
            return new CleanupFailure($"{login.LoginProvider}: {ex.GetBaseException().Message}", ex);
        }
    }

    /// <summary>
    /// A fresh read once a failure has emptied the context: the old copy may carry a stale concurrency
    /// stamp, and every save of it would fail (#981).
    /// </summary>
    private async Task<ApplicationUser> FindAccountAsync(Guid userId) =>
        await _dbContext.Users.FindAsync(userId)
        ?? throw new InvalidOperationException($"User {userId} is not in the database.");

    /// <summary>
    /// Locks the account for good until the next finalizer run. It writes only the lockout columns, straight
    /// to the database: the tracked account still holds the anonymized values, and saving them would hide
    /// the account from the finalizer, which finds work by the real address (#908, #937).
    /// </summary>
    /// <remarks>
    /// An account that no longer waits is left alone. The security stamp stays, because a new one would
    /// only break the cancel link the owner may hold. A failure is returned, not logged: whether a lock was
    /// needed is known only after the read that follows (#980, #1018). No lock and no failure means no row
    /// matched.
    /// </remarks>
    private async Task<(bool Locked, Exception? Failure)> TryLockAccountAsync(Guid userId)
    {
        try
        {
            DateTimeOffset? lockedUntil = DateTimeOffset.MaxValue;

            // So a write that read the account before the lock fails instead of putting the old lockout back.
            string concurrencyStamp = Guid.NewGuid().ToString();

            int lockedCount = await _dbContext.Users
                .Where(u => u.Id == userId
                            && u.DeletionScheduledAt != null
                            && !u.Email!.EndsWith(AnonymizationConstants.EmailDomain))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(u => u.LockoutEnabled, true)
                        .SetProperty(u => u.LockoutEnd, lockedUntil)
                        .SetProperty(u => u.ConcurrencyStamp, concurrencyStamp),
                    CancellationToken.None);

            if (lockedCount == 0)
            {
                return (false, null);
            }

            LogEmergencyLockout(_logger, userId);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private static string Describe(IdentityResult result) =>
        string.Join(", ", result.Errors.Select(e => e.Description));

    [LoggerMessage(EventId = EventIds.GdprErasureInitiated, Level = LogLevel.Information, Message = "GDPR erasure initiated for user {UserId}")]
    private static partial void LogGdprErasureInitiated(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureAnonymizationFailed, Level = LogLevel.Critical, Message = "Failed to anonymize user {UserId}. The finalizer will retry. Errors: {Errors}")]
    private static partial void LogAnonymizationFailed(ILogger logger, Guid userId, string errors);

    [LoggerMessage(EventId = EventIds.GdprErasureAuthAnonymized, Level = LogLevel.Information, Message = "GDPR erasure: auth data anonymized for user {UserId}")]
    private static partial void LogAuthDataAnonymized(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureAuthFailed, Level = LogLevel.Critical, Message = "Auth-side GDPR erasure failed for user {UserId}. The finalizer will retry.")]
    private static partial void LogAuthSideErasureFailed(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureNoLongerWaiting, Message = "GDPR erasure of user {UserId} stopped after a failed save: the account no longer waits for its erasure. Its owner cancelled the deletion, or another run erased it. No lockout and no retry needed. Errors: {Errors}")]
    private static partial void LogNoLongerWaiting(ILogger logger, LogLevel level, Exception? exception, Guid userId, string errors);

    [LoggerMessage(EventId = EventIds.GdprErasureSaveLandedAfterAll, Level = LogLevel.Information, Message = "GDPR erasure: the save for user {UserId} was reported as failed, but it landed. The account carries the address this run wrote, so the erasure goes on with the cleanup. Errors: {Errors}")]
    private static partial void LogSaveLandedAfterAll(ILogger logger, Exception? exception, Guid userId, string errors);

    [LoggerMessage(EventId = EventIds.GdprErasureSaveOutcomeUnknown, Level = LogLevel.Warning, Message = "GDPR erasure of user {UserId} stopped after a failed save: the account no longer waits for its erasure, so no run comes back to it. The check whether this run's own save landed failed. If it did land, the account is erased, but its tokens, roles, claims and logins were not cleaned up. Errors: {Errors}")]
    private static partial void LogSaveOutcomeUnknown(ILogger logger, Exception exception, Guid userId, string errors);

    [LoggerMessage(EventId = EventIds.GdprErasureAccountDeleted, Level = LogLevel.Information, Message = "Account deleted (anonymized) for user {UserId}")]
    private static partial void LogAccountDeleted(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureArtifactsCleaned, Level = LogLevel.Information, Message = "GDPR erasure: tokens, authorizations, roles, claims, and logins cleaned up for user {UserId}")]
    private static partial void LogArtifactsCleaned(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureArtifactsCleanupFailed, Level = LogLevel.Warning, Message = "GDPR erasure: some steps of the auth artifact cleanup failed for user {UserId}. Failed: {FailedSteps}. The account is already anonymized and locked. Its tokens expire on their own. Anything else that was not removed stays until someone removes it.")]
    private static partial void LogArtifactsCleanupFailed(ILogger logger, Exception? exception, Guid userId, string failedSteps);

    [LoggerMessage(EventId = EventIds.GdprErasureEmergencyLockout, Level = LogLevel.Warning, Message = "Emergency lockout applied for user {UserId} after a failed GDPR erasure")]
    private static partial void LogEmergencyLockout(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureEmergencyLockoutFailed, Level = LogLevel.Critical, Message = "Failed to apply emergency lockout for user {UserId}. Manual intervention required immediately.")]
    private static partial void LogEmergencyLockoutFailed(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureUnneededLockoutFailed, Level = LogLevel.Warning, Message = "GDPR erasure: the emergency lockout for user {UserId} failed, but no lockout was needed, because the account no longer waits for its erasure.")]
    private static partial void LogUnneededLockoutFailed(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureLockoutFailedOnLockedAccount, Level = LogLevel.Warning, Message = "GDPR erasure: the emergency lockout for user {UserId} failed, but a read of the account in this run found it already locked for good, so nobody has to lock it by hand.")]
    private static partial void LogLockoutFailedOnLockedAccount(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureSaveCheckFailedAfterFailedLock, Level = LogLevel.Warning, Message = "GDPR erasure of user {UserId}: after the emergency lockout failed, the check whether this run's own save landed failed too. The account is taken to still wait, so the lines after this one promise a retry. If the save did land, the account is erased and no run comes back to it, so its tokens, roles, claims and logins are never cleaned up.")]
    private static partial void LogSaveCheckFailedAfterFailedLock(ILogger logger, Exception exception, Guid userId);

    /// <summary>
    /// Identity reports a lost concurrency check as errors; for anything else the exception's message
    /// stands in.
    /// </summary>
    private sealed record FailedSave(string Errors, Exception? Exception);

    /// <summary>
    /// The first two never hold at once: a landed save wrote the anonymized address, so the account no
    /// longer waits. LockedForGood matters only while it still waits.
    /// </summary>
    private sealed record AccountState(bool SaveLanded, bool StillWaits, bool LockedForGood);

    private sealed record CleanupFailure(string Reason, Exception? Exception);
}
