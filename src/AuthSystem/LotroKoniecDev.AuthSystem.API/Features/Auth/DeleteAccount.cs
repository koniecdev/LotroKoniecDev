using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Schedules a GDPR deletion instead of doing it at once (ADR-0031): the account is locked for the grace
/// period and a single-use cancel link is e-mailed, so a stolen password alone cannot erase it. The
/// cancel e-mail's outbox row commits with the schedule (ADR-0038).
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
        private readonly IUserSessionRevoker _sessionRevoker;
        private readonly OutboxWriter _outboxWriter;
        private readonly IAccountDeletionSchedule _deletionSchedule;
        private readonly IPasswordConfirmationThrottle _confirmationThrottle;
        private readonly IAccountDeletionScheduleThrottle _scheduleThrottle;
        private readonly TimeProvider _timeProvider;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IUserSessionRevoker sessionRevoker,
            OutboxWriter outboxWriter,
            IAccountDeletionSchedule deletionSchedule,
            IPasswordConfirmationThrottle confirmationThrottle,
            IAccountDeletionScheduleThrottle scheduleThrottle,
            TimeProvider timeProvider,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _sessionRevoker = sessionRevoker;
            _outboxWriter = outboxWriter;
            _deletionSchedule = deletionSchedule;
            _confirmationThrottle = confirmationThrottle;
            _scheduleThrottle = scheduleThrottle;
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

            // The permit comes before the account is loaded, so a refusal costs no database read and
            // probing the account's state costs a permit too (ADR-0053).
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

            // Last, so only a schedule that would happen spends a permit. Cancelling returns a reset
            // token, so without this an account could loop schedule, cancel and reset (#811).
            if (!_scheduleThrottle.TryAcquire(user.Id))
            {
                LogDeletionScheduleThrottled(_logger, user.Id);
                return Result.Failure<ScheduledDeletion>(AuthErrors.DeletionScheduleThrottled);
            }

            DateTimeOffset scheduledAt = _timeProvider.GetUtcNow();

            // An armed undo pushes this date out, so the account cannot be erased while an undo link that
            // cancels the deletion is still in an inbox (#685). The finalizer reads the same rule.
            DateTimeOffset finalizesAt =
                _deletionSchedule.FinalizesAt(scheduledAt, user.EmailChangeRevertArmedAt);

            // Locked while the deletion is pending, so neither the owner nor an attacker can use it.
            user.DeletionScheduledAt = scheduledAt;
            user.LockoutEnabled = true;
            user.LockoutEnd = finalizesAt;

            // In the same save as the outbox row (ADR-0038 decision 2): the dispatch can create the cancel
            // token milliseconds after the commit, and a later stamp change would break it.
            user.SecurityStamp = Guid.NewGuid().ToString();

            _outboxWriter.Enqueue(new AccountDeletionScheduled(user.Id));

            IdentityResult updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                string errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                LogSchedulingUpdateFailed(_logger, user.Id, errors);
                return Result.Failure<ScheduledDeletion>(AuthErrors.DeletionSchedulingFailed);
            }

            // An access token already issued keeps working until it expires (ADR-0049).
            await _sessionRevoker.RevokeAllAsync(user.Id.ToString());

            LogDeletionScheduled(_logger, user.Id, finalizesAt, command.IpAddress, command.UserAgent);

            return Result.Success(new ScheduledDeletion(scheduledAt, finalizesAt));
        }

        [LoggerMessage(EventId = EventIds.GdprDeletionScheduled, Level = LogLevel.Information, Message = "GDPR deletion scheduled for user {UserId}; finalizes at {FinalizesAt}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogDeletionScheduled(ILogger logger, Guid userId, DateTimeOffset finalizesAt, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.GdprDeletionSchedulingUpdateFailed, Level = LogLevel.Error, Message = "Failed to persist the deletion schedule for user {UserId}: {Errors}")]
        private static partial void LogSchedulingUpdateFailed(ILogger logger, Guid userId, string errors);

        [LoggerMessage(EventId = EventIds.PasswordConfirmationThrottled, Level = LogLevel.Warning, Message = "Account deletion refused for user {UserId}: the password confirmation budget is spent")]
        private static partial void LogPasswordConfirmationThrottled(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.GdprDeletionScheduleThrottled, Level = LogLevel.Warning, Message = "Account deletion refused for user {UserId}: the deletion schedule budget is spent")]
        private static partial void LogDeletionScheduleThrottled(ILogger logger, Guid userId);
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
                string? userId = user.FindUserId();

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
            // Off the per-IP policies (ADR-0053): the brakes are the handler's two per-account budgets,
            // password confirmations and deletion schedules (#811).
            .DisableRateLimiting()
            .WithName(nameof(DeleteAccount))
            .WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    public const string DeletionScheduledAtHeader = "X-Deletion-Scheduled-At";
    public const string DeletionFinalizesAtHeader = "X-Deletion-Finalizes-At";
}
