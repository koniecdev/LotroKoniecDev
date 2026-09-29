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

    public async Task<Result> EraseAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        LogGdprErasureInitiated(_logger, user.Id);

        // If any step here fails we still have to try to lock the account, so the data cannot be
        // reached while the finalizer retries on its next run.
        try
        {
            // Anonymize the auth user data first, which is the core GDPR requirement.
            // DeletionScheduledAt stays set. It is not personal data and it records when the erasure
            // was asked for.
            string anonymizedGuid = Guid.NewGuid().ToString("N");
            user.UserName = anonymizedGuid;
            user.NormalizedUserName = anonymizedGuid.ToUpperInvariant();
            user.Email = $"{AnonymizationConstants.EmailPrefix}{anonymizedGuid}{AnonymizationConstants.EmailDomain}";
            user.NormalizedEmail = $"{AnonymizationConstants.EmailPrefix.ToUpperInvariant()}{anonymizedGuid.ToUpperInvariant()}{AnonymizationConstants.EmailDomain.ToUpperInvariant()}";
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
                string errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                LogAnonymizationFailed(_logger, user.Id, errors);
                await TryLockAccountAsync(user.Id, cancellationToken);
                return Result.Failure(AuthErrors.AccountDeletionFailed(AnonymizationFailedDetails));
            }

            LogAuthDataAnonymized(_logger, user.Id);
        }
        catch (Exception ex)
        {
            LogAuthSideErasureFailed(_logger, ex, user.Id);
            await TryLockAccountAsync(user.Id, cancellationToken);
            return Result.Failure(AuthErrors.AccountDeletionFailed(AnonymizationFailedDetails));
        }

        // Best-effort cleanup: revoke the tokens and remove roles, claims and logins. The account is
        // already anonymized and locked, so a failure here does not break GDPR compliance.
        await CleanupAuthArtifactsAsync(user, cancellationToken);

        LogAccountDeleted(_logger, user.Id);

        return Result.Success();
    }

    private async Task CleanupAuthArtifactsAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        try
        {
            string userId = user.Id.ToString();

            await foreach (object token in _tokenManager.FindBySubjectAsync(userId, cancellationToken))
            {
                await _tokenManager.TryRevokeAsync(token, cancellationToken);
            }

            await foreach (object authorization in _authorizationManager.FindBySubjectAsync(userId, cancellationToken))
            {
                await _authorizationManager.TryRevokeAsync(authorization, cancellationToken);
            }

            IList<string> roles = await _userManager.GetRolesAsync(user);
            if (roles.Count > 0)
            {
                await _userManager.RemoveFromRolesAsync(user, roles);
            }

            IList<Claim> claims = await _userManager.GetClaimsAsync(user);
            if (claims.Count > 0)
            {
                await _userManager.RemoveClaimsAsync(user, claims);
            }

            IList<UserLoginInfo> logins = await _userManager.GetLoginsAsync(user);
            foreach (UserLoginInfo login in logins)
            {
                await _userManager.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
            }

            LogArtifactsCleaned(_logger, user.Id);
        }
        catch (Exception ex)
        {
            LogArtifactsCleanupFailed(_logger, ex, user.Id);
        }
    }

    /// <summary>
    /// Locks the account for good after a failed erasure, so its data cannot be reached while it waits
    /// for the finalizer's next run. It writes the lockout columns and nothing else, straight to the
    /// database. The failed save left the anonymized values on the tracked account, and any save of that
    /// account would write them too. The finalizer finds its work by the real address, so an account
    /// saved with the anonymized one would never be retried and its cleanup would never run (#908, #937).
    /// </summary>
    /// <remarks>
    /// An account that is no longer waiting for its erasure is left alone: its owner cancelled the
    /// deletion, or another run already erased the account and locked it in the same save.
    /// </remarks>
    private async Task TryLockAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset? lockedUntil = DateTimeOffset.MaxValue;
            string securityStamp = Guid.NewGuid().ToString();

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
                        .SetProperty(u => u.SecurityStamp, securityStamp)
                        .SetProperty(u => u.ConcurrencyStamp, concurrencyStamp),
                    cancellationToken);

            if (lockedCount == 0)
            {
                LogEmergencyLockoutNotNeeded(_logger, userId);
                return;
            }

            LogEmergencyLockout(_logger, userId);
        }
        catch (Exception ex)
        {
            LogEmergencyLockoutFailed(_logger, ex, userId);
        }
    }

    [LoggerMessage(EventId = EventIds.GdprErasureInitiated, Level = LogLevel.Information, Message = "GDPR erasure initiated for user {UserId}")]
    private static partial void LogGdprErasureInitiated(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureAnonymizationFailed, Level = LogLevel.Critical, Message = "Failed to anonymize user {UserId}. The finalizer will retry. Errors: {Errors}")]
    private static partial void LogAnonymizationFailed(ILogger logger, Guid userId, string errors);

    [LoggerMessage(EventId = EventIds.GdprErasureAuthAnonymized, Level = LogLevel.Information, Message = "GDPR erasure: auth data anonymized for user {UserId}")]
    private static partial void LogAuthDataAnonymized(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureAuthFailed, Level = LogLevel.Critical, Message = "Auth-side GDPR erasure failed for user {UserId}. The finalizer will retry.")]
    private static partial void LogAuthSideErasureFailed(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureAccountDeleted, Level = LogLevel.Information, Message = "Account deleted (anonymized) for user {UserId}")]
    private static partial void LogAccountDeleted(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureArtifactsCleaned, Level = LogLevel.Information, Message = "GDPR erasure: tokens, authorizations, roles, claims, and logins cleaned up for user {UserId}")]
    private static partial void LogArtifactsCleaned(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureArtifactsCleanupFailed, Level = LogLevel.Warning, Message = "GDPR erasure: cleanup of auth artifacts failed for user {UserId}. The account is already anonymized and locked. Its tokens expire on their own, but its roles, claims and logins stay until someone removes them.")]
    private static partial void LogArtifactsCleanupFailed(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureEmergencyLockout, Level = LogLevel.Warning, Message = "Emergency lockout applied for user {UserId} after a failed GDPR erasure")]
    private static partial void LogEmergencyLockout(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureEmergencyLockoutNotNeeded, Level = LogLevel.Information, Message = "No emergency lockout for user {UserId} after a failed GDPR erasure: the account is no longer waiting for its erasure")]
    private static partial void LogEmergencyLockoutNotNeeded(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureEmergencyLockoutFailed, Level = LogLevel.Critical, Message = "Failed to apply emergency lockout for user {UserId}. Manual intervention required immediately.")]
    private static partial void LogEmergencyLockoutFailed(ILogger logger, Exception exception, Guid userId);
}
