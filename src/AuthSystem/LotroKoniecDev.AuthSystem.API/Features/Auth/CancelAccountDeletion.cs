using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Cancels a scheduled GDPR account deletion with the single-use token from the cancellation e-mail
/// (ADR-0031). It needs no login on purpose: the account is locked out for the whole grace period, so
/// the token in the e-mail is the only proof that the person owns the account.
/// Cancelling also invalidates the current password, which may be all an attacker has, and returns a
/// fresh reset token that sends the user into the password reset flow.
/// The notice e-mail goes through the outbox pipeline (ADR-0038): its row commits together with the
/// cancellation, so a send that used to be best-effort is now guaranteed.
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
        /// A hash computed up front, so the not-found path takes as long as the normal one.
        /// </summary>
        private static readonly string DummyPasswordHash =
            new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly OutboxWriter _outboxWriter;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            OutboxWriter outboxWriter,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _outboxWriter = outboxWriter;
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

            // Every path pays the same PBKDF2 cost. Running the dummy hash only when the user is not
            // found would make real accounts answer measurably faster, because their path is only a
            // cheap DataProtector check, and the response time would then tell an attacker which
            // accounts exist.
            _ = _userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

            // An unknown e-mail, no scheduled deletion and a bad token all return the same general
            // error, so nobody can use this endpoint to learn the state of an account.
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

            // Whoever asked for the deletion may hold the current password, so the password is
            // invalidated with the cancellation. Only the reset flow brings the account back.
            user.DeletionScheduledAt = null;
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;
            user.PasswordHash = null;

            // Changing the security stamp makes the cancel token single-use and ends any sessions that
            // are still open. It happens inside the same save that commits the outbox row (ADR-0038
            // decision 2). This notice carries no token, but the rule keeps every e-mail writer's stamp
            // final before the relay can see its row.
            user.SecurityStamp = Guid.NewGuid().ToString();

            _outboxWriter.Enqueue(new AccountDeletionCancelled(user.Id));

            IdentityResult updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                string errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                return Result.Failure<CancelledDeletion>(AuthErrors.CancelDeletionFailed(errors));
            }

            _outboxWriter.NotifyEnqueuedCommitted();

            // Created after the commit, from the saved stamp. A token created before the save would be
            // invalidated by the very stamp change it travels with.
            string passwordResetToken = await _userManager.GeneratePasswordResetTokenAsync(user);

            LogDeletionCancelled(_logger, user.Id, command.IpAddress, command.UserAgent);

            return Result.Success(new CancelledDeletion(passwordResetToken));
        }

        [LoggerMessage(EventId = EventIds.GdprDeletionCancelled, Level = LogLevel.Information, Message = "GDPR deletion cancelled for user {UserId}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogDeletionCancelled(ILogger logger, Guid userId, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.GdprCancelTokenInvalid, Level = LogLevel.Warning, Message = "Invalid cancel-deletion token presented for {MaskedEmail}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogCancelTokenInvalid(ILogger logger, string maskedEmail, string? ipAddress, string? userAgent);
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
