using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Cancels a scheduled GDPR account deletion using the one-time token from the
/// cancellation email (ADR-0031). Anonymous by design — the account is locked out for
/// the whole grace window, so the emailed token is the only proof of ownership.
/// Cancelling invalidates the current password (it may be the attacker's only asset)
/// and hands back a fresh reset token that forces the password-reset flow.
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
        /// Pre-computed hash for timing-equalization when user is not found.
        /// </summary>
        private static readonly string DummyPasswordHash =
            new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAccountDeletionEmailSender _emailSender;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IAccountDeletionEmailSender emailSender,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _emailSender = emailSender;
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

            string maskedEmail = command.Email.MaskEmail();
            ApplicationUser? user = await _userManager.FindByEmailAsync(command.Email);

            // Every path pays the same PBKDF2 cost. Burning the dummy hash only on the
            // not-found branch would make existing accounts answer measurably FASTER
            // (their path is just a cheap DataProtector check), turning response time
            // into an inverted user-enumeration oracle.
            _ = _userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

            // Unknown email, no scheduled deletion and a bad token all collapse into the same
            // generic error so the endpoint can't be used to probe account state.
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

            // The deletion may have been requested by whoever holds the current password,
            // so the password dies with the cancellation; only the reset flow brings the
            // account back.
            user.DeletionScheduledAt = null;
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;
            user.PasswordHash = null;

            IdentityResult updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                string errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                return Result.Failure<CancelledDeletion>(AuthErrors.CancelDeletionFailed(errors));
            }

            // Rotating the stamp makes the cancel token single-use and kills any leftover
            // sessions; the reset token must be generated AFTER the rotation to stay valid.
            IdentityResult stampResult = await _userManager.UpdateSecurityStampAsync(user);
            if (!stampResult.Succeeded)
            {
                LogSecurityStampUpdateFailed(_logger, user.Id);
            }

            string passwordResetToken = await _userManager.GeneratePasswordResetTokenAsync(user);

            Result emailResult = await _emailSender.SendDeletionCancelledEmailAsync(
                user.Email!, cancellationToken);
            if (emailResult.IsFailure)
            {
                LogCancelledEmailFailed(_logger, user.Id, emailResult.Error.Message);
            }

            LogDeletionCancelled(_logger, user.Id, command.IpAddress, command.UserAgent);

            return Result.Success(new CancelledDeletion(passwordResetToken));
        }

        [LoggerMessage(EventId = EventIds.GdprDeletionCancelled, Level = LogLevel.Information, Message = "GDPR deletion cancelled for user {UserId}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogDeletionCancelled(ILogger logger, Guid userId, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.GdprCancelTokenInvalid, Level = LogLevel.Warning, Message = "Invalid cancel-deletion token presented for {MaskedEmail}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogCancelTokenInvalid(ILogger logger, string maskedEmail, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.GdprDeletionCancelledEmailFailed, Level = LogLevel.Error, Message = "Failed to send the deletion-cancelled confirmation email for user {UserId}: {Error}")]
        private static partial void LogCancelledEmailFailed(ILogger logger, Guid userId, string error);

        [LoggerMessage(EventId = EventIds.GdprDeletionCancelStampFailed, Level = LogLevel.Error, Message = "Failed to update security stamp for user {UserId} while cancelling deletion")]
        private static partial void LogSecurityStampUpdateFailed(ILogger logger, Guid userId);
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
