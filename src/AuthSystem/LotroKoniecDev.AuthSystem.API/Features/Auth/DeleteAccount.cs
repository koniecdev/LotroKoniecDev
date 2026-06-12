using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class DeleteAccount : IApiEndpoint
{
    internal sealed record Command(
        string UserId,
        string Password) : ICommand<Result>;

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(x => x.UserId)
                .NotEmpty().WithMessage("User ID is required.");

            RuleFor(x => x.Password)
                .NotEmpty().WithMessage("Password is required to confirm account deletion.");
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result>
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IOpenIddictTokenManager _tokenManager;
        private readonly IOpenIddictAuthorizationManager _authorizationManager;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IOpenIddictTokenManager tokenManager,
            IOpenIddictAuthorizationManager authorizationManager,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _tokenManager = tokenManager;
            _authorizationManager = authorizationManager;
            _validator = validator;
            _logger = logger;
        }

        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure(validationResult.ToValidationError(nameof(DeleteAccount)));
            }

            ApplicationUser? user = await _userManager.FindByIdAsync(command.UserId);
            if (user is null)
            {
                return Result.Failure(AuthErrors.UserNotFound);
            }

            LogGdprErasureInitiated(_logger, user.Id);

            bool passwordValid = await _userManager.CheckPasswordAsync(user, command.Password);
            if (!passwordValid)
            {
                return Result.Failure(AuthErrors.InvalidCurrentPassword);
            }

            // No cross-context archival call (the KittySaver pattern is deliberately not lifted):
            // the TranslationSystem stores only opaque IdentityId attribution references, which
            // become non-attributable once the auth user below is anonymized.
            // If any auth-side step fails, we must still try to lock the account
            // so the data isn't accessible while support resolves the issue.
            try
            {
                // Anonymize auth user data first (core GDPR requirement)
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

                IdentityResult updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    string errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                    LogAnonymizationFailed(_logger, user.Id, errors);
                    await TryLockAccountAsync(user);
                    return Result.Failure(AuthErrors.AccountDeletionFailed(
                        "Account deletion partially failed. Your account has been locked for security. Please contact support."));
                }

                LogAuthDataAnonymized(_logger, user.Id);

                // Invalidate all sessions
                await _userManager.UpdateSecurityStampAsync(user);

                // Lock account permanently
                await _userManager.SetLockoutEnabledAsync(user, true);
                await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            }
            catch (Exception ex)
            {
                LogAuthSideErasureFailed(_logger, ex, user.Id);
                await TryLockAccountAsync(user);
                return Result.Failure(AuthErrors.AccountDeletionFailed(
                    "Account deletion partially failed. Your account has been locked for security. Please contact support."));
            }

            // Best-effort cleanup: revoke tokens, remove roles/claims/logins.
            // The account is already anonymized and locked at this point,
            // so failures here don't compromise GDPR compliance.
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

        [LoggerMessage(EventId = EventIds.GdprErasureInitiated, Level = LogLevel.Information, Message = "GDPR erasure request initiated for user {UserId}")]
        private static partial void LogGdprErasureInitiated(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.GdprErasureAnonymizationFailed, Level = LogLevel.Critical, Message = "Failed to anonymize user {UserId}. Manual cleanup required. Errors: {Errors}")]
        private static partial void LogAnonymizationFailed(ILogger logger, Guid userId, string errors);

        [LoggerMessage(EventId = EventIds.GdprErasureAuthAnonymized, Level = LogLevel.Information, Message = "GDPR erasure: auth data anonymized for user {UserId}")]
        private static partial void LogAuthDataAnonymized(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.GdprErasureAuthFailed, Level = LogLevel.Critical, Message = "Auth-side GDPR erasure failed for user {UserId}. Manual cleanup required.")]
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

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/account/delete", async (
                DeleteAccountRequest request,
                ClaimsPrincipal user,
                ICommandHandler<Command, Result> handler,
                CancellationToken cancellationToken) =>
            {
                string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? user.FindFirstValue(OpenIddictConstants.Claims.Subject);

                if (string.IsNullOrEmpty(userId))
                {
                    return Results.Unauthorized();
                }

                Command command = new(userId, request.Password);

                Result commandResult = await handler.Handle(command, cancellationToken);

                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.NoContent();
            })
            .RequireAuthorization()
            .RequireRateLimiting("auth-endpoint-limit")
            .WithName(nameof(DeleteAccount))
            .WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }
}
