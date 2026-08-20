using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
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
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IOpenIddictTokenManager _tokenManager;
    private readonly IOpenIddictAuthorizationManager _authorizationManager;
    private readonly ILogger<AccountErasureService> _logger;

    public AccountErasureService(
        UserManager<ApplicationUser> userManager,
        IOpenIddictTokenManager tokenManager,
        IOpenIddictAuthorizationManager authorizationManager,
        ILogger<AccountErasureService> logger)
    {
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

            // The permanent lockout goes in the same update as the anonymization marker. The finalizer
            // picks its work by the marker alone, so a user must never end up marked but not locked. A
            // separate lockout write could fail, and that user would then be skipped by every later
            // run.
            user.LockoutEnabled = true;
            user.LockoutEnd = DateTimeOffset.MaxValue;

            IdentityResult updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                string errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                LogAnonymizationFailed(_logger, user.Id, errors);
                await TryLockAccountAsync(user);
                return Result.Failure(AuthErrors.AccountDeletionFailed(
                    "Account erasure partially failed. The account stays locked; the finalizer will retry."));
            }

            LogAuthDataAnonymized(_logger, user.Id);

            // End every session.
            await _userManager.UpdateSecurityStampAsync(user);
        }
        catch (Exception ex)
        {
            LogAuthSideErasureFailed(_logger, ex, user.Id);
            await TryLockAccountAsync(user);
            return Result.Failure(AuthErrors.AccountDeletionFailed(
                "Account erasure partially failed. The account stays locked; the finalizer will retry."));
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

    private async Task TryLockAccountAsync(ApplicationUser user)
    {
        try
        {
            await _userManager.SetLockoutEnabledAsync(user, true);
            await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            await _userManager.UpdateSecurityStampAsync(user);

            LogEmergencyLockout(_logger, user.Id);
        }
        catch (Exception ex)
        {
            LogEmergencyLockoutFailed(_logger, ex, user.Id);
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

    [LoggerMessage(EventId = EventIds.GdprErasureArtifactsCleanupFailed, Level = LogLevel.Warning, Message = "GDPR erasure: cleanup of auth artifacts failed for user {UserId}. Account is already anonymized and locked — artifacts will expire naturally.")]
    private static partial void LogArtifactsCleanupFailed(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureEmergencyLockout, Level = LogLevel.Warning, Message = "Emergency lockout applied for user {UserId} after partial GDPR erasure failure")]
    private static partial void LogEmergencyLockout(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.GdprErasureEmergencyLockoutFailed, Level = LogLevel.Critical, Message = "Failed to apply emergency lockout for user {UserId}. Manual intervention required immediately.")]
    private static partial void LogEmergencyLockoutFailed(ILogger logger, Exception exception, Guid userId);
}
