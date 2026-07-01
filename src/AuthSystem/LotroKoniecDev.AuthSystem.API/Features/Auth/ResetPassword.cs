using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class ResetPassword : IApiEndpoint
{
    internal sealed record Command(
        string Email,
        string Token,
        string NewPassword) : ICommand<Result>;

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
                .NotEmpty().WithMessage("Reset token is required.");

            RuleFor(x => x.NewPassword).ApplyPasswordRules();
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
        private readonly IUserSessionRevoker _sessionRevoker;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IUserSessionRevoker sessionRevoker,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _sessionRevoker = sessionRevoker;
            _validator = validator;
            _logger = logger;
        }

        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure(validationResult.ToValidationError(nameof(ResetPassword)));
            }

            ApplicationUser? user = await _userManager.FindByEmailAsync(command.Email);

            if (user is null)
            {
                // Perform dummy work to prevent timing-based user enumeration
                _ = new PasswordHasher<ApplicationUser>()
                    .VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

                return Result.Failure(AuthErrors.InvalidPasswordResetToken);
            }

            IdentityResult identityResult = await _userManager.ResetPasswordAsync(user, command.Token, command.NewPassword);

            if (!identityResult.Succeeded)
            {
                if (identityResult.Errors.Any(e => e.Code is "InvalidToken"))
                {
                    return Result.Failure(AuthErrors.InvalidPasswordResetToken);
                }

                string errors = string.Join(", ", identityResult.Errors.Select(e => e.Description));
                LogPasswordResetFailed(_logger, user.Id, errors);
                return Result.Failure(AuthErrors.PasswordResetFailed(errors));
            }

            IdentityResult stampResult = await _userManager.UpdateSecurityStampAsync(user);
            if (!stampResult.Succeeded)
            {
                LogSecurityStampUpdateFailed(_logger, user.Id);
            }

            await _sessionRevoker.RevokeAllAsync(user.Id.ToString(), cancellationToken);

            return Result.Success();
        }

        [LoggerMessage(EventId = EventIds.ResetPasswordFailed, Level = LogLevel.Warning, Message = "Password reset failed for user {UserId}. Errors: {Errors}")]
        private static partial void LogPasswordResetFailed(ILogger logger, Guid userId, string errors);

        [LoggerMessage(EventId = EventIds.ResetPasswordSecurityStampFailed, Level = LogLevel.Error, Message = "Failed to update security stamp for user {UserId} after password change")]
        private static partial void LogSecurityStampUpdateFailed(ILogger logger, Guid userId);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/reset-password", async (
                ResetPasswordRequest request,
                ICommandHandler<Command, Result> handler,
                CancellationToken cancellationToken) =>
            {
                Command command = new(request.Email, request.Token, request.NewPassword);

                Result commandResult = await handler.Handle(command, cancellationToken);

                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.Ok();
            })
            .AllowAnonymous()
            .RequireRateLimiting("auth-endpoint-limit")
            .WithName("ResetPassword")
            .WithTags("Authentication")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
