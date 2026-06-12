using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class ChangePassword : IApiEndpoint
{
    internal sealed record Command(
        string UserId,
        string CurrentPassword,
        string NewPassword) : ICommand<Result>;

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(x => x.UserId)
                .NotEmpty().WithMessage("User ID is required.");

            RuleFor(x => x.CurrentPassword)
                .NotEmpty().WithMessage("Current password is required.");

            RuleFor(x => x.NewPassword).ApplyPasswordRules();
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result>
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _validator = validator;
            _logger = logger;
        }

        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure(validationResult.ToValidationError(nameof(ChangePassword)));
            }

            ApplicationUser? user = await _userManager.FindByIdAsync(command.UserId);

            if (user is null)
            {
                return Result.Failure(AuthErrors.UserNotFound);
            }

            IdentityResult identityResult = await _userManager.ChangePasswordAsync(
                user,
                command.CurrentPassword,
                command.NewPassword);

            if (identityResult.Succeeded)
            {
                IdentityResult stampResult = await _userManager.UpdateSecurityStampAsync(user);
                if (!stampResult.Succeeded)
                {
                    LogSecurityStampUpdateFailed(_logger, user.Id);
                }

                return Result.Success();
            }

            if (identityResult.Errors.Any(identityError => identityError.Code is "PasswordMismatch"))
            {
                return Result.Failure(AuthErrors.InvalidCurrentPassword);
            }

            string errors = string.Join(", ", identityResult.Errors.Select(e => e.Description));
            LogPasswordChangeFailed(_logger, user.Id, errors);

            return Result.Failure(AuthErrors.PasswordChangeFailed(errors));
        }

        [LoggerMessage(EventId = EventIds.ChangePasswordSecurityStampFailed, Level = LogLevel.Error, Message = "Failed to update security stamp for user {UserId} after password change")]
        private static partial void LogSecurityStampUpdateFailed(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.ChangePasswordFailed, Level = LogLevel.Warning, Message = "Password change failed for user {UserId}. Errors: {Errors}")]
        private static partial void LogPasswordChangeFailed(ILogger logger, Guid userId, string errors);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/change-password", async (
                ChangePasswordRequest request,
                ClaimsPrincipal user,
                ICommandHandler<Command, Result> handler,
                CancellationToken cancellationToken) =>
            {
                string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? user.FindFirstValue(OpenIddict.Abstractions.OpenIddictConstants.Claims.Subject);

                if (string.IsNullOrEmpty(userId))
                {
                    return Results.Unauthorized();
                }

                Command command = new(userId, request.CurrentPassword, request.NewPassword);

                Result commandResult = await handler.Handle(command, cancellationToken);

                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.Ok();
            })
            .RequireAuthorization()
            .RequireRateLimiting("auth-endpoint-limit")
            .WithName(nameof(ChangePassword))
            .WithTags("Authentication")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
