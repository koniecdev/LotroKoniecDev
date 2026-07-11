using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class ForgotPassword : IApiEndpoint
{
    internal sealed record Command(string Email) : ICommand<Result>;

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
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result>
    {
        /// <summary>
        /// Pre-computed hash for timing-equalization when user is not found.
        /// </summary>
        private static readonly string DummyPasswordHash =
            new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IPasswordResetEmailSender _emailSender;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IPasswordResetEmailSender emailSender,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _emailSender = emailSender;
            _validator = validator;
            _logger = logger;
        }

        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure(validationResult.ToValidationError(nameof(ForgotPassword)));
            }

            ApplicationUser? user = await _userManager.FindByEmailAsync(command.Email);

            if (user is null)
            {
                // Perform dummy work to prevent timing-based user enumeration
                _ = new PasswordHasher<ApplicationUser>()
                    .VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

                string maskedEmail = command.Email.MaskEmail();
                LogPasswordResetNonExistent(_logger, maskedEmail);

                // Always return success to prevent email enumeration
                return Result.Success();
            }

            // While GDPR deletion is scheduled, the emailed cancel-deletion link is the only
            // recovery path — a password reset would neither unlock the account nor stop the
            // deletion. Pretend success so account state can't be probed.
            if (user.DeletionScheduledAt is not null)
            {
                LogPasswordResetSkippedDeletionScheduled(_logger, user.Id);
                return Result.Success();
            }

            string token = await _userManager.GeneratePasswordResetTokenAsync(user);

            Result emailResult = await _emailSender.SendPasswordResetEmailAsync(user.Id, command.Email, token, cancellationToken);
            if (emailResult.IsFailure)
            {
                LogPasswordResetEmailFailed(_logger, user.Id, emailResult.Error.Message);
            }

            return Result.Success();
        }

        [LoggerMessage(EventId = EventIds.ForgotPasswordNonExistent, Level = LogLevel.Information, Message = "Password reset requested for non-existent email {Email}")]
        private static partial void LogPasswordResetNonExistent(ILogger logger, string email);

        [LoggerMessage(EventId = EventIds.ForgotPasswordEmailFailed, Level = LogLevel.Error, Message = "Failed to send password reset email for user {UserId}: {Error}")]
        private static partial void LogPasswordResetEmailFailed(ILogger logger, Guid userId, string error);

        [LoggerMessage(EventId = EventIds.ForgotPasswordDeletionScheduled, Level = LogLevel.Information, Message = "Password reset skipped for user {UserId}: account deletion is scheduled")]
        private static partial void LogPasswordResetSkippedDeletionScheduled(ILogger logger, Guid userId);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/forgot-password", async (
                ForgotPasswordRequest request,
                ICommandHandler<Command, Result> handler,
                CancellationToken cancellationToken) =>
            {
                Command command = new(request.Email);

                Result commandResult = await handler.Handle(command, cancellationToken);

                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.Ok();
            })
            .AllowAnonymous()
            .RequireRateLimiting("forgot-password-limit")
            .WithName(nameof(ForgotPassword))
            .WithTags("Authentication")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
