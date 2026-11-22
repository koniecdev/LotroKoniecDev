using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
/// The admin takes the Reviewer role away (#1488). Taking a right away is never refused for the account's
/// state, and a call on an account without the role changes nothing, an overlapping call included.
/// </summary>
internal sealed partial class RemoveReviewerRole : IApiEndpoint
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

        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure(validationResult.ToValidationError(nameof(RemoveReviewerRole)));
            }

            ApplicationUser? account = await _userManager.FindByIdAsync(command.AccountId.ToString());
            if (account is null)
            {
                return Result.Failure(AuthErrors.UserNotFound);
            }

            if (!await _userManager.IsInRoleAsync(account, AuthConstants.Roles.Reviewer))
            {
                return Result.Success();
            }

            IdentityResult identityResult;
            try
            {
                identityResult = await _userManager.RemoveFromRoleAsync(account, AuthConstants.Roles.Reviewer);
            }
            catch (DbUpdateException)
            {
                // An overlapping call deleted the same role row first.
                if (await IsReviewerAsync(account))
                {
                    throw;
                }

                LogReviewerRoleAlreadyChanged(_logger, account.Id);
                return Result.Success();
            }

            if (!identityResult.Succeeded)
            {
                // A concurrency failure is also how an overlapping call that won shows up here.
                if (!await IsReviewerAsync(account))
                {
                    LogReviewerRoleAlreadyChanged(_logger, account.Id);
                    return Result.Success();
                }

                string errors = string.Join(", ", identityResult.Errors.Select(error => error.Code));
                LogReviewerRoleChangeFailed(_logger, account.Id, errors);
                return Result.Failure(AuthErrors.ReviewerRoleChangeFailed);
            }

            // Best effort. The token endpoint's role check ends a session this misses (#1488).
            await _sessionRevoker.RevokeAllAsync(account.Id.ToString());

            LogReviewerRoleRemoved(_logger, account.Id, command.AdminUserId);

            return Result.Success();
        }

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

        // GetRolesAsync queries the database, not the change tracker, so it sees what the other call saved.
        private async Task<bool> IsReviewerAsync(ApplicationUser account) =>
            (await _userManager.GetRolesAsync(account)).Contains(AuthConstants.Roles.Reviewer);

        [LoggerMessage(EventId = EventIds.ReviewerRoleRemoved, Level = LogLevel.Information, Message = "Reviewer role taken from user {UserId} by admin {AdminUserId}")]
        private static partial void LogReviewerRoleRemoved(ILogger logger, Guid userId, string adminUserId);

        [LoggerMessage(EventId = EventIds.ReviewerRoleChangeFailed, Level = LogLevel.Error, Message = "Reviewer role could not be taken from user {UserId}. Errors: {Errors}")]
        private static partial void LogReviewerRoleChangeFailed(ILogger logger, Guid userId, string errors);

        [LoggerMessage(EventId = EventIds.ReviewerRoleAlreadyChanged, Level = LogLevel.Information, Message = "Reviewer role for user {UserId} was already taken away by an overlapping call")]
        private static partial void LogReviewerRoleAlreadyChanged(ILogger logger, Guid userId);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapDelete("auth/admin/accounts/{accountId:guid}/reviewer", async (
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
            .WithName(nameof(RemoveReviewerRole))
            .WithTags("Admin")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }
}
