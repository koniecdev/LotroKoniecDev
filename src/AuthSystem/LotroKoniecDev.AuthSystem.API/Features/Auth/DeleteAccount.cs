using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Schedules a GDPR account deletion instead of doing it at once (ADR-0031). The account is locked
/// for the grace period and a single-use cancel link is e-mailed, so a stolen password alone can no
/// longer erase an account for good. The finalizer does the real erasure once the grace period is
/// over.
/// The cancel e-mail goes through the outbox pipeline (ADR-0038): its row commits together with the
/// schedule, so "scheduled but no e-mail recorded" cannot happen, and retrying a failed delivery is
/// the pipeline's job, not this handler's.
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
        private readonly IAccountDeletionSchedule _deletionSchedule;
        private readonly IPasswordConfirmationThrottle _confirmationThrottle;
        private readonly TimeProvider _timeProvider;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IOpenIddictTokenManager tokenManager,
            IOpenIddictAuthorizationManager authorizationManager,
            OutboxWriter outboxWriter,
            IAccountDeletionSchedule deletionSchedule,
            IPasswordConfirmationThrottle confirmationThrottle,
            TimeProvider timeProvider,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _tokenManager = tokenManager;
            _authorizationManager = authorizationManager;
            _outboxWriter = outboxWriter;
            _deletionSchedule = deletionSchedule;
            _confirmationThrottle = confirmationThrottle;
            _timeProvider = timeProvider;
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

            // The permit comes first: after validation, before the account is even loaded. A refused
            // request then costs no database read, a burst of guesses cannot slip past the gate, and every
            // later refusal sits behind it, so probing an account's state costs a permit too (ADR-0053).
            // The key is the id the token names; a token this server signed always carries one.
            if (!Guid.TryParse(command.UserId, out Guid userId))
            {
                return Result.Failure<ScheduledDeletion>(AuthErrors.UserNotFound);
            }

            if (!_confirmationThrottle.TryAcquire(userId))
            {
                LogPasswordConfirmationThrottled(_logger, userId);
                return Result.Failure<ScheduledDeletion>(AuthErrors.PasswordConfirmationThrottled);
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

            // An armed undo pushes this date out, so the account cannot be erased while a link that
            // cancels the deletion is still in somebody's inbox (#685). The finalizer reads the same
            // rule, so the header below is a promise it keeps.
            DateTimeOffset finalizesAt =
                _deletionSchedule.FinalizesAt(scheduledAt, user.EmailChangeRevertArmedAt);

            // Lock the account for as long as the deletion is pending, so neither the person who asked
            // nor a possible attacker can use it. Nothing is erased here: the data stays until the
            // finalizer runs.
            user.DeletionScheduledAt = scheduledAt;
            user.LockoutEnabled = true;
            user.LockoutEnd = finalizesAt;

            // End every session by setting the new security stamp inside the same save that commits
            // the outbox row (ADR-0038 decision 2). The relay works on a signal, so the dispatch
            // processor can create the cancel token milliseconds after the commit. A stamp changed in
            // a later save would break the link that was just created.
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
            // Best effort. Refresh tokens are reference tokens, so revoking them stops them at once.
            // Access tokens carry their own claims, so one already issued keeps working until it
            // expires. That is five minutes, and ADR-0049 is why.
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

        [LoggerMessage(EventId = EventIds.PasswordConfirmationThrottled, Level = LogLevel.Warning, Message = "Account deletion refused for user {UserId}: the password confirmation budget is spent")]
        private static partial void LogPasswordConfirmationThrottled(ILogger logger, Guid userId);
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
            // Off the per-IP policies on purpose (ADR-0053). Every call here comes from the frontend, so an
            // IP key is one bucket for every user, and one user's traffic could refuse another's deletion.
            // The brake is the per-account confirmation budget in the handler.
            .DisableRateLimiting()
            .WithName(nameof(DeleteAccount))
            .WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    public const string DeletionScheduledAtHeader = "X-Deletion-Scheduled-At";
    public const string DeletionFinalizesAtHeader = "X-Deletion-Finalizes-At";
}
