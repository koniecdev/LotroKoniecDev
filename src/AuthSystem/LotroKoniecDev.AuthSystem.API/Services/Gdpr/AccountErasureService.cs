using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Services.Gdpr;

/// <summary>
/// The part of a GDPR account deletion that cannot be undone: anonymizing the auth data, locking the
/// account for good and cleaning up what is left. The finalizer calls it once the grace period is over
/// (see ADR-0031).
/// Nothing has to be called in the other context: the TranslationSystem only stores IdentityId values
/// as credit, and once the auth user is anonymized those values point at nobody.
/// It is safe to run twice, because callers skip users whose e-mail already carries the anonymization
/// marker.
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
    private readonly ILogger<AccountErasureService> _logger;

    public AccountErasureService(
        AuthDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        ILogger<AccountErasureService> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _tokenManager = tokenManager;
        _authorizationManager = authorizationManager;
        _logger = logger;
    }

    public async Task<Result<AccountErasureOutcome>> EraseAsync(ApplicationUser user)
    {
        LogGdprErasureInitiated(_logger, user.Id);

        string anonymizedGuid = Guid.NewGuid().ToString("N");
        string anonymizedEmail = $"{AnonymizationConstants.EmailPrefix}{anonymizedGuid}{AnonymizationConstants.EmailDomain}";
        FailedSave? failedSave = null;

        try
        {
            // A stored sign-in session holds the name and e-mail too (ADR-0062). It goes before the
            // anonymizing save: the finalizer retries an account only until that save lands, so a delete
            // that came after it and failed would never run again. It runs only while the account still
            // waits, by the emergency lock's rule: an owner who cancelled meanwhile may have signed in
            // again.
            Guid userId = user.Id;
            await _dbContext.SignInSessions
                .Where(session => session.UserId == userId
                                  && _dbContext.Users.Any(u => u.Id == userId
                                                               && u.DeletionScheduledAt != null
                                                               && !u.Email!.EndsWith(AnonymizationConstants.EmailDomain)))
                .ExecuteDeleteAsync(CancellationToken.None);

            // Then anonymize the auth user data, which is the core GDPR requirement.
            // DeletionScheduledAt stays set. It is not personal data and it records when the erasure
            // was asked for.
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

            // The armed undo target is a former address of this person, so it is personal data and
            // goes with the rest. Clearing it also releases the address reservation of #684 — an
            // erased account must not keep somebody else's address blocked — and closes the undo on
            // an account there is no longer anything to undo for.
            user.DisarmEmailChangeRevert();

            // The permanent lockout and the new security stamp, which ends every session, go in the
            // same update as the anonymization marker. The finalizer picks its work by the marker
            // alone, so no later run retries anything that comes after this save. A separate write
            // that failed there would never be done (#908).
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
                await ResolveFailedSaveAsync(user.Id, anonymizedEmail, failedSave);

            if (afterFailedSave.IsFailure || afterFailedSave.Value is AccountErasureOutcome.NoLongerWaiting)
            {
                return afterFailedSave;
            }
        }

        LogAuthDataAnonymized(_logger, user.Id);

        // Best-effort cleanup: revoke the tokens and remove roles, claims and logins. The account is
        // already anonymized and locked, so a failure here does not break GDPR compliance.
        await CleanupAuthArtifactsAsync(user.Id);

        LogAccountDeleted(_logger, user.Id);

        return AccountErasureOutcome.Erased;
    }

    /// <summary>
    /// Finds out what a failed erasure save means before anything about it is logged, so the log never
    /// promises a retry that will not come (#962). A lock that landed means the account still waits and
    /// the next run retries it. In every other case this run reads the account. Its own save may have
    /// landed with only the answer lost: EF's retry then ran the save again with the old concurrency
    /// stamp, and Identity reported the conflict. Or the account may no longer wait, because its owner
    /// cancelled or another run erased it, and then no run comes back to it. A failed lock is reported as
    /// a failed lock only when the read says a lock was needed, or when the read fails too (#980). A lock
    /// is needed only while the account is not locked for good yet. An earlier run's lock, or this run's
    /// own lock whose answer was lost, may have locked it already, and then the failed lock is no reason
    /// to call for urgent help (#1018).
    /// </summary>
    private async Task<Result<AccountErasureOutcome>> ResolveFailedSaveAsync(
        Guid userId,
        string anonymizedEmail,
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
            // The address is new for every attempt, so only this run's own save can have written it.
            // "Still waits" is the lock's own rule, and "locked for good" is what the lock writes.
            DateTimeOffset? lockedForGood = DateTimeOffset.MaxValue;
            state = await _dbContext.Users
                .Where(u => u.Id == userId)
                .Select(u => new AccountState(
                    u.Email == anonymizedEmail,
                    u.DeletionScheduledAt != null && !u.Email!.EndsWith(AnonymizationConstants.EmailDomain),
                    u.LockoutEnabled && u.LockoutEnd == lockedForGood))
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

            // Nothing is known about the account, so it is taken to still wait and not to be locked, as
            // before #980: the retry is assumed, and the lock's line asks a person to look.
            LogSaveCheckFailedAfterFailedLock(_logger, ex, userId);
            state = new AccountState(SaveLanded: false, StillWaits: true, LockedForGood: false);
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
            // The failed save was one SaveChanges, and its account row landed, so all of it landed. Its
            // changes count as saved, as they would after a save that answered. Otherwise the next save in
            // the cleanup would write them again against the old concurrency stamp and fail.
            _dbContext.ChangeTracker.AcceptAllChanges();
            LogSaveLandedAfterAll(_logger, failedSave.Exception, userId, failedSave.Errors);
            return AccountErasureOutcome.Erased;
        }

        // A lost concurrency check is what a race looks like. An exception is a real database error that
        // happened to meet a race, and the next account may hit it too.
        LogLevel level = failedSave.Exception is null ? LogLevel.Information : LogLevel.Warning;
        LogNoLongerWaiting(_logger, level, failedSave.Exception, userId, failedSave.Errors);
        return AccountErasureOutcome.NoLongerWaiting;
    }

    /// <summary>
    /// The account still waits, or may still wait. If it does, the next run of the finalizer picks it up
    /// again.
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
    /// Every step runs, even after an earlier one failed, and the log names each step that failed.
    /// Identity reports a lost concurrency check as a failed result, and OpenIddict reports any failed
    /// revoke as <c>false</c>, so each result is checked (#962).
    /// All steps share one context, and a failed write leaves its changes in it. Identity never undoes
    /// them, and OpenIddict undoes them only after a lost concurrency check. The next save would send
    /// them again and fail with them, so every failure empties the context first (#981).
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
    /// OpenIddict's token manager writes a token again even when it is already revoked. Its
    /// authorization manager does not. Scheduling the deletion has usually revoked the tokens already,
    /// so this skips a revoked token: the write would change nothing, and if the prune removed the row
    /// in the meantime, it would log a false failure.
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
    /// Catches its own exception, so the logins after one that fails are still removed.
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
    /// The copy the context tracks, or a new read once a failure has emptied the context. The copy from
    /// before the failure may carry a concurrency stamp that was never saved, or one another write has
    /// replaced, and every save of it would fail on that stamp (#981).
    /// </summary>
    private async Task<ApplicationUser> FindAccountAsync(Guid userId) =>
        await _dbContext.Users.FindAsync(userId)
        ?? throw new InvalidOperationException($"User {userId} is not in the database.");

    /// <summary>
    /// Locks the account for good after a failed erasure, so its data cannot be reached while it waits
    /// for the finalizer's next run. It writes the lockout columns and nothing else, straight to the
    /// database. The failed save left the anonymized values on the tracked account, and any save of that
    /// account would write them too. The finalizer finds its work by the real address, so an account
    /// saved with the anonymized one would never be retried and its cleanup would never run (#908, #937).
    /// </summary>
    /// <remarks>
    /// An account that is no longer waiting for its erasure is left alone: its owner cancelled the
    /// deletion, another run already erased the account and locked it in the same save, or this run's own
    /// save landed although it was reported as failed.
    /// The security stamp stays as it is. Scheduling the deletion already ended every session, and
    /// sign-in is refused while a deletion is scheduled. A new stamp would only break the cancel link
    /// the owner may still hold.
    /// A failed lock is not logged here, only returned. Whether a lock was needed at all is known only
    /// after the read that follows it. An account that no longer waits needs none (#980), and neither
    /// does one that is locked for good already (#1018). A lock that neither landed nor failed matched no
    /// row.
    /// </remarks>
    private async Task<(bool Locked, Exception? Failure)> TryLockAccountAsync(Guid userId)
    {
        try
        {
            DateTimeOffset? lockedUntil = DateTimeOffset.MaxValue;

            // A new concurrency stamp makes a write that read the account before the lock fail, instead
            // of putting the old lockout back.
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

    [LoggerMessage(EventId = EventIds.GdprErasureLockoutFailedOnLockedAccount, Level = LogLevel.Warning, Message = "GDPR erasure: the emergency lockout for user {UserId} failed, but the account is already locked for good, so nothing can reach it while it waits for the next run.")]
    private static partial void LogLockoutFailedOnLockedAccount(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureSaveCheckFailedAfterFailedLock, Level = LogLevel.Warning, Message = "GDPR erasure of user {UserId}: after the emergency lockout failed, the check whether this run's own save landed failed too. The account is taken to still wait, so the lines after this one promise a retry. If the save did land, the account is erased and no run comes back to it, so its tokens, roles, claims and logins are never cleaned up.")]
    private static partial void LogSaveCheckFailedAfterFailedLock(ILogger logger, Exception exception, Guid userId);

    /// <summary>
    /// Identity reports a lost concurrency check as errors. Anything else arrives as an exception, and
    /// its message stands in for the errors.
    /// </summary>
    private sealed record FailedSave(string Errors, Exception? Exception);

    /// <summary>
    /// The account as the read after a failed save finds it. The first two never hold at once: a landed
    /// save wrote the anonymized address, so the account no longer waits. Whether the account is locked
    /// for good matters only while it still waits.
    /// </summary>
    private sealed record AccountState(bool SaveLanded, bool StillWaits, bool LockedForGood);

    /// <summary>
    /// One write the cleanup could not make. An exception's message stands in for the reason.
    /// </summary>
    private sealed record CleanupFailure(string Reason, Exception? Exception);
}
