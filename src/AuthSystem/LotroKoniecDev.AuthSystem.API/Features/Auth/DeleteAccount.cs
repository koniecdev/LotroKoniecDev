using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Schedules GDPR account deletion instead of executing it immediately (ADR-0031):
/// the account is locked for the grace period and a one-time cancellation link is emailed,
/// so a stolen password alone can no longer erase an account irreversibly.
/// The deletion finalizer performs the actual erasure once the grace period elapses.
/// The cancel e-mail travels through the outbox pipeline (ADR-0038): its row commits atomically
/// with the schedule, so "scheduled but no e-mail ever recorded" cannot exist, and delivery
/// failures are the pipeline's to retry — not this handler's to compensate.
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
        private readonly OutboxWriter _outboxWriter;
        private readonly TimeProvider _timeProvider;
        private readonly GdprSettings _gdprSettings;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IOpenIddictTokenManager tokenManager,
            IOpenIddictAuthorizationManager authorizationManager,
            OutboxWriter outboxWriter,
            TimeProvider timeProvider,
            IOptions<GdprSettings> gdprSettings,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _tokenManager = tokenManager;
            _authorizationManager = authorizationManager;
            _outboxWriter = outboxWriter;
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

            DateTimeOffset scheduledAt = _timeProvider.GetUtcNow();
            DateTimeOffset finalizesAt = scheduledAt + _gdprSettings.DeletionGracePeriod;

            // Lock the account for the whole grace window so neither the requester nor
            // a potential attacker can use it. Nothing is erased here — data stays intact
            // until the finalizer runs after the grace period.
            user.DeletionScheduledAt = scheduledAt;
            user.LockoutEnabled = true;
            user.LockoutEnd = finalizesAt;

            // Invalidate all sessions by assigning the fresh stamp INSIDE the same save that
            // commits the outbox row (ADR-0038 decision 2): the relay is signal-driven, so the
            // dispatch processor can mint the cancel token milliseconds after commit — a stamp
            // rotated in a later save would kill the emailed link it just minted.
            user.SecurityStamp = Guid.NewGuid().ToString();

            _outboxWriter.Enqueue(new AccountDeletionScheduled(user.Id));

            IdentityResult updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                string errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                LogSchedulingUpdateFailed(_logger, user.Id, errors);
                return Result.Failure<ScheduledDeletion>(AuthErrors.DeletionSchedulingFailed);
            }

            _outboxWriter.NotifyEnqueuedCommitted();

            await TryRevokeOpenIddictArtifactsAsync(user, cancellationToken);

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

        [LoggerMessage(EventId = EventIds.GdprDeletionScheduled, Level = LogLevel.Information, Message = "GDPR deletion scheduled for user {UserId}; finalizes at {FinalizesAt}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogDeletionScheduled(ILogger logger, Guid userId, DateTimeOffset finalizesAt, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.GdprDeletionSchedulingUpdateFailed, Level = LogLevel.Error, Message = "Failed to persist the deletion schedule for user {UserId}: {Errors}")]
        private static partial void LogSchedulingUpdateFailed(ILogger logger, Guid userId, string errors);

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
