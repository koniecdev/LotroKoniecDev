using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Schedules GDPR account deletion instead of executing it immediately (ADR-0031):
/// the account is locked for the grace period and a one-time cancellation link is emailed,
/// so a stolen password alone can no longer erase an account irreversibly.
/// The deletion finalizer performs the actual erasure once the grace period elapses.
/// </summary>
internal sealed partial class DeleteAccount : IApiEndpoint
{
    internal sealed record Command(
        string UserId,
        string Password,
        string? IpAddress,
        string? UserAgent) : ICommand<Result<ScheduledDeletion>>;

    internal sealed record ScheduledDeletion(
        DateTimeOffset ScheduledAt,
        DateTimeOffset FinalizesAt);

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

    internal sealed partial class Handler : ICommandHandler<Command, Result<ScheduledDeletion>>
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IOpenIddictTokenManager _tokenManager;
        private readonly IOpenIddictAuthorizationManager _authorizationManager;
        private readonly IAccountDeletionEmailSender _emailSender;
        private readonly TimeProvider _timeProvider;
        private readonly GdprSettings _gdprSettings;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IOpenIddictTokenManager tokenManager,
            IOpenIddictAuthorizationManager authorizationManager,
            IAccountDeletionEmailSender emailSender,
            TimeProvider timeProvider,
            IOptions<GdprSettings> gdprSettings,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _tokenManager = tokenManager;
            _authorizationManager = authorizationManager;
            _emailSender = emailSender;
            _timeProvider = timeProvider;
            _gdprSettings = gdprSettings.Value;
            _validator = validator;
            _logger = logger;
        }

        public async ValueTask<Result<ScheduledDeletion>> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure<ScheduledDeletion>(validationResult.ToValidationError(nameof(DeleteAccount)));
            }

            ApplicationUser? user = await _userManager.FindByIdAsync(command.UserId);
            if (user is null)
            {
                return Result.Failure<ScheduledDeletion>(AuthErrors.UserNotFound);
            }

            bool passwordValid = await _userManager.CheckPasswordAsync(user, command.Password);
            if (!passwordValid)
            {
                return Result.Failure<ScheduledDeletion>(AuthErrors.InvalidCurrentPassword);
            }

            if (user.DeletionScheduledAt is not null)
            {
                return Result.Failure<ScheduledDeletion>(AuthErrors.DeletionAlreadyScheduled);
            }

            // The still-real email address; captured before any mutation so the
            // cancellation link always reaches the legitimate owner.
            string email = user.Email!;

            DateTimeOffset scheduledAt = _timeProvider.GetUtcNow();
            DateTimeOffset finalizesAt = scheduledAt + _gdprSettings.DeletionGracePeriod;

            // Lock the account for the whole grace window so neither the requester nor
            // a potential attacker can use it. Nothing is erased here — data stays intact
            // until the finalizer runs after the grace period.
            user.DeletionScheduledAt = scheduledAt;
            user.LockoutEnabled = true;
            user.LockoutEnd = finalizesAt;

            IdentityResult updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                string errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                LogSchedulingUpdateFailed(_logger, user.Id, errors);
                return Result.Failure<ScheduledDeletion>(AuthErrors.DeletionSchedulingFailed);
            }

            // Invalidate all sessions; the cancel token must be generated AFTER the stamp
            // rotation because it binds to the security stamp (one-time-use guarantee).
            IdentityResult stampResult = await _userManager.UpdateSecurityStampAsync(user);
            if (!stampResult.Succeeded)
            {
                LogSecurityStampUpdateFailed(_logger, user.Id);
            }

            await TryRevokeOpenIddictArtifactsAsync(user, cancellationToken);

            string cancelToken = await _userManager.GenerateUserTokenAsync(
                user,
                AccountDeletionCancellationTokenProvider.ProviderName,
                AccountDeletionCancellationTokenProvider.CancelDeletionPurpose);

            Result emailResult = await _emailSender.SendDeletionScheduledEmailAsync(
                email, cancelToken, finalizesAt, cancellationToken);
            if (emailResult.IsFailure)
            {
                // Without the emailed link the owner has no way to cancel, so the schedule
                // is unwound and the user is asked to retry once mail delivery recovers.
                LogScheduledEmailFailed(_logger, user.Id, emailResult.Error.Message);
                await TryUnwindScheduleAsync(user);
                return Result.Failure<ScheduledDeletion>(AuthErrors.DeletionSchedulingFailed);
            }

            LogDeletionScheduled(_logger, user.Id, finalizesAt, command.IpAddress, command.UserAgent);

            return Result.Success(new ScheduledDeletion(scheduledAt, finalizesAt));
        }

        private async Task TryRevokeOpenIddictArtifactsAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            // Best effort: refresh tokens are reference tokens, so revocation cuts them off
            // immediately; self-contained access tokens simply expire within the hour.
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
            }
            catch (Exception ex)
            {
                LogArtifactRevocationFailed(_logger, ex, user.Id);
            }
        }

        private async Task TryUnwindScheduleAsync(ApplicationUser user)
        {
            try
            {
                user.DeletionScheduledAt = null;
                user.LockoutEnd = null;

                IdentityResult updateResult = await _userManager.UpdateAsync(user);
                if (updateResult.Succeeded)
                {
                    LogScheduleUnwound(_logger, user.Id);
                    return;
                }

                string errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                LogScheduleUnwindFailed(_logger, user.Id, errors);
            }
            catch (Exception ex)
            {
                LogScheduleUnwindException(_logger, ex, user.Id);
            }
        }

        [LoggerMessage(EventId = EventIds.GdprDeletionScheduled, Level = LogLevel.Information, Message = "GDPR deletion scheduled for user {UserId}; finalizes at {FinalizesAt}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogDeletionScheduled(ILogger logger, Guid userId, DateTimeOffset finalizesAt, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.GdprDeletionScheduledEmailFailed, Level = LogLevel.Error, Message = "Failed to send the deletion-scheduled email for user {UserId}: {Error}. Unwinding the schedule.")]
        private static partial void LogScheduledEmailFailed(ILogger logger, Guid userId, string error);

        [LoggerMessage(EventId = EventIds.GdprDeletionScheduleUnwound, Level = LogLevel.Warning, Message = "Deletion schedule unwound for user {UserId} because the cancellation email could not be sent")]
        private static partial void LogScheduleUnwound(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.GdprDeletionScheduleUnwindFailed, Level = LogLevel.Critical, Message = "Failed to unwind the deletion schedule for user {UserId}: {Errors}. Account stays locked with a schedule but without a cancellation email. Manual intervention required.")]
        private static partial void LogScheduleUnwindFailed(ILogger logger, Guid userId, string errors);

        [LoggerMessage(EventId = EventIds.GdprDeletionScheduleUnwindException, Level = LogLevel.Critical, Message = "Exception while unwinding the deletion schedule for user {UserId}. Manual intervention required.")]
        private static partial void LogScheduleUnwindException(ILogger logger, Exception exception, Guid userId);

        [LoggerMessage(EventId = EventIds.GdprDeletionSchedulingUpdateFailed, Level = LogLevel.Error, Message = "Failed to persist the deletion schedule for user {UserId}: {Errors}")]
        private static partial void LogSchedulingUpdateFailed(ILogger logger, Guid userId, string errors);

        [LoggerMessage(EventId = EventIds.GdprDeletionScheduleStampFailed, Level = LogLevel.Error, Message = "Failed to update security stamp for user {UserId} while scheduling deletion")]
        private static partial void LogSecurityStampUpdateFailed(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.GdprDeletionScheduleArtifactRevocationFailed, Level = LogLevel.Warning, Message = "Failed to revoke OpenIddict artifacts for user {UserId} while scheduling deletion. Refresh tokens may stay valid until expiry.")]
        private static partial void LogArtifactRevocationFailed(ILogger logger, Exception exception, Guid userId);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/account/delete", async (
                DeleteAccountRequest request,
                ClaimsPrincipal user,
                HttpContext httpContext,
                ICommandHandler<Command, Result<ScheduledDeletion>> handler,
                CancellationToken cancellationToken) =>
            {
                string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? user.FindFirstValue(OpenIddictConstants.Claims.Subject);

                if (string.IsNullOrEmpty(userId))
                {
                    return Results.Unauthorized();
                }

                Command command = new(
                    userId,
                    request.Password,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    httpContext.Request.Headers.UserAgent.ToString());

                Result<ScheduledDeletion> commandResult = await handler.Handle(command, cancellationToken);

                if (commandResult.IsFailure)
                {
                    return Results.Problem(commandResult.Error.ToProblemDetails());
                }

                httpContext.Response.Headers[DeletionScheduledAtHeader] =
                    commandResult.Value.ScheduledAt.ToString("O");
                httpContext.Response.Headers[DeletionFinalizesAtHeader] =
                    commandResult.Value.FinalizesAt.ToString("O");

                return Results.NoContent();
            })
            .RequireAuthorization()
            .RequireRateLimiting("auth-endpoint-limit")
            .WithName(nameof(DeleteAccount))
            .WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    public const string DeletionScheduledAtHeader = "X-Deletion-Scheduled-At";
    public const string DeletionFinalizesAtHeader = "X-Deletion-Finalizes-At";
}
