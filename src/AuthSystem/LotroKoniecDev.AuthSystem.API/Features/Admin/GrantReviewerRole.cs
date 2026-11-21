using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Admin;

/// <summary>
/// The admin gives an account the Reviewer role (#1488). The account is signed out, so it signs in again
/// with the new role. A repeated call changes nothing, because the website retries a write that timed out.
/// </summary>
internal sealed partial class GrantReviewerRole : IApiEndpoint
{
    internal sealed record Command(string AdminUserId, Guid AccountId) : ICommand<Result>;

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(x => x.AdminUserId)
                .NotEmpty().WithMessage("Admin user ID is required.");

            RuleFor(x => x.AccountId)
                .NotEmpty().WithMessage("Account ID is required.");
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result>
    {
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
                return Result.Failure(validationResult.ToValidationError(nameof(GrantReviewerRole)));
            }

            ApplicationUser? account = await _userManager.FindByIdAsync(command.AccountId.ToString());
            if (account is null)
            {
                return Result.Failure(AuthErrors.UserNotFound);
            }

            IList<string> roles = await _userManager.GetRolesAsync(account);
            if (roles.Contains(AuthConstants.Roles.Reviewer))
            {
                return Result.Success();
            }

            if (!ReviewerRole.CanBeGranted(roles, isDeletionScheduled: account.DeletionScheduledAt is not null))
            {
                return Result.Failure(AuthErrors.ReviewerRoleNotAllowed);
            }

            IdentityResult identityResult = await _userManager.AddToRoleAsync(account, AuthConstants.Roles.Reviewer);
            if (!identityResult.Succeeded)
            {
                string errors = string.Join(", ", identityResult.Errors.Select(error => error.Code));
                LogReviewerRoleChangeFailed(_logger, account.Id, errors);
                return Result.Failure(AuthErrors.ReviewerRoleChangeFailed);
            }

            // Best effort. The token endpoint's role check ends a session this misses (#1488).
            await _sessionRevoker.RevokeAllAsync(account.Id.ToString());

            LogReviewerRoleGranted(_logger, account.Id, command.AdminUserId);

            return Result.Success();
        }

        [LoggerMessage(EventId = EventIds.ReviewerRoleGranted, Level = LogLevel.Information, Message = "Reviewer role given to user {UserId} by admin {AdminUserId}")]
        private static partial void LogReviewerRoleGranted(ILogger logger, Guid userId, string adminUserId);

        [LoggerMessage(EventId = EventIds.ReviewerRoleChangeFailed, Level = LogLevel.Error, Message = "Reviewer role could not be given to user {UserId}. Errors: {Errors}")]
        private static partial void LogReviewerRoleChangeFailed(ILogger logger, Guid userId, string errors);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPut("auth/admin/accounts/{accountId:guid}/reviewer", async (
                Guid accountId,
                ClaimsPrincipal user,
                ICommandHandler<Command, Result> handler,
                CancellationToken cancellationToken) =>
            {
                string? adminUserId = user.FindUserId();

                if (string.IsNullOrEmpty(adminUserId))
                {
                    return Results.Unauthorized();
                }

                Result commandResult = await handler.Handle(new Command(adminUserId, accountId), cancellationToken);

                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.NoContent();
            })
            .RequireAuthorization(AdminTokenPolicy.Name)
            .WithName(nameof(GrantReviewerRole))
            .WithTags("Admin")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
