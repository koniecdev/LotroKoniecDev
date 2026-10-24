using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Needs no login: the account is locked for the grace period, so the e-mailed token is the only proof
/// of ownership (ADR-0031). Cancelling also invalidates the current password, which may be all an
/// attacker has, and returns a fresh reset token.
/// </summary>
internal sealed partial class CancelAccountDeletion : IApiEndpoint
{
    internal sealed record Command(
        string Email,
        string Token,
        string? IpAddress,
        string? UserAgent) : ICommand<Result<CancelledDeletion>>;

    internal sealed record CancelledDeletion(string PasswordResetToken);

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(x => x.Email)
                .NotEmpty().WithMessage("A valid email address is required.")
                .MaximumLength(EmailConstants.MaxLength)
                    .WithMessage($"Email must not exceed {EmailConstants.MaxLength} characters.")
                .Matches(EmailConstants.RegexPattern)
                    .WithMessage("A valid email address is required.");

            RuleFor(x => x.Token)
                .NotEmpty().WithMessage("Cancellation token is required.");
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result<CancelledDeletion>>
    {
        /// <summary>
        /// Lets every path verify exactly one hash.
        /// </summary>
        private static readonly string DummyPasswordHash =
            new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly OutboxWriter _outboxWriter;
        private readonly IAccountDeletionSchedule _deletionSchedule;
        private readonly TimeProvider _timeProvider;
        private readonly IResponseTimeFloor _responseTimeFloor;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            OutboxWriter outboxWriter,
            IAccountDeletionSchedule deletionSchedule,
            TimeProvider timeProvider,
            IResponseTimeFloor responseTimeFloor,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _outboxWriter = outboxWriter;
            _deletionSchedule = deletionSchedule;
            _timeProvider = timeProvider;
            _responseTimeFloor = responseTimeFloor;
            _validator = validator;
            _logger = logger;
        }

        public async ValueTask<Result<CancelledDeletion>> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure<CancelledDeletion>(validationResult.ToValidationError(nameof(CancelAccountDeletion)));
            }

            // ADR-0059: the answer hides whether an address has an account.
            return await _responseTimeFloor.HoldAsync(
                ResponseTimeFloors.AccountLookup,
                () => CancelAsync(command));
        }

        private async Task<Result<CancelledDeletion>> CancelAsync(Command command)
        {
            string maskedEmail = command.Email.MaskEmail();
            ApplicationUser? user = await _userManager.FindByEmailAsync(command.Email);

            // On every path: a real account's path is only a cheap token check, so a dummy hash only for
            // unknown addresses would make real accounts answer faster.
            _ = _userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

            // An unknown e-mail, no scheduled deletion and a bad token answer the same way.
            if (user is null)
            {
                LogCancelTokenInvalid(_logger, maskedEmail, command.IpAddress, command.UserAgent);
                return Result.Failure<CancelledDeletion>(AuthErrors.InvalidCancelDeletionToken);
            }

            bool tokenValid = await _userManager.VerifyUserTokenAsync(
                user,
                AccountDeletionCancellationTokenProvider.ProviderName,
                AccountDeletionCancellationTokenProvider.CancelDeletionPurpose,
                command.Token);

            if (!tokenValid || user.DeletionScheduledAt is null)
            {
                LogCancelTokenInvalid(_logger, maskedEmail, command.IpAddress, command.UserAgent);
                return Result.Failure<CancelledDeletion>(AuthErrors.InvalidCancelDeletionToken);
            }

            // A token from a late e-mail is still valid, but the link ends with the window (#947).
            DateTimeOffset scheduledAt = user.DeletionScheduledAt.Value;
            if (!_deletionSchedule.IsCancellableAt(scheduledAt, _timeProvider.GetUtcNow()))
            {
                LogCancelWindowOver(
                    _logger,
                    user.Id,
                    _deletionSchedule.CancellableUntil(scheduledAt),
                    command.IpAddress,
                    command.UserAgent);
                return Result.Failure<CancelledDeletion>(AuthErrors.InvalidCancelDeletionToken);
            }

            // Whoever asked for the deletion may hold the password. Only the reset flow brings it back.
            user.DeletionScheduledAt = null;
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;
            user.PasswordHash = null;

            // Makes the cancel token single-use. In the same save as the outbox row, as every e-mail
            // writer does (ADR-0038 decision 2).
            user.SecurityStamp = Guid.NewGuid().ToString();

            _outboxWriter.Enqueue(new AccountDeletionCancelled(user.Id));

            IdentityResult updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                string errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                return Result.Failure<CancelledDeletion>(AuthErrors.CancelDeletionFailed(errors));
            }

            // After the save: a token made before would die with the stamp change it travels with.
            string passwordResetToken = await _userManager.GeneratePasswordResetTokenAsync(user);

            LogDeletionCancelled(_logger, user.Id, command.IpAddress, command.UserAgent);

            return Result.Success(new CancelledDeletion(passwordResetToken));
        }

        [LoggerMessage(EventId = EventIds.GdprDeletionCancelled, Level = LogLevel.Information, Message = "GDPR deletion cancelled for user {UserId}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogDeletionCancelled(ILogger logger, Guid userId, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.GdprCancelTokenInvalid, Level = LogLevel.Warning, Message = "Invalid cancel-deletion token presented for {MaskedEmail}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogCancelTokenInvalid(ILogger logger, string maskedEmail, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.GdprCancelWindowOver, Level = LogLevel.Information, Message = "Cancel-deletion link refused for user {UserId}: the cancel window ended at {CancellableUntil}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogCancelWindowOver(ILogger logger, Guid userId, DateTimeOffset cancellableUntil, string? ipAddress, string? userAgent);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/account/cancel-deletion", async (
                CancelAccountDeletionRequest request,
                HttpContext httpContext,
                ICommandHandler<Command, Result<CancelledDeletion>> handler,
                CancellationToken cancellationToken) =>
            {
                Command command = new(
                    request.Email,
                    request.Token,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    httpContext.Request.Headers.UserAgent.ToString());

                Result<CancelledDeletion> commandResult = await handler.Handle(command, cancellationToken);

                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.Ok(new CancelAccountDeletionResponse(commandResult.Value.PasswordResetToken));
            })
            .AllowAnonymous()
            .RequireRateLimiting("auth-endpoint-limit")
            .WithName(nameof(CancelAccountDeletion))
            .WithTags("Account")
            .Produces<CancelAccountDeletionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
