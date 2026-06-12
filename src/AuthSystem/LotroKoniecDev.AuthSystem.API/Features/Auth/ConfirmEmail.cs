using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class ConfirmEmail : IApiEndpoint
{
    internal sealed record Command(string Email, string Token) : ICommand<Result>;

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
                .NotEmpty().WithMessage("Confirmation token is required.");
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
                return Result.Failure(validationResult.ToValidationError(nameof(ConfirmEmail)));
            }

            ApplicationUser? user = await _userManager.FindByEmailAsync(command.Email);

            if (user is null)
            {
                // Perform dummy work to prevent timing-based user enumeration
                _ = new PasswordHasher<ApplicationUser>()
                    .VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

                return Result.Failure(AuthErrors.InvalidEmailConfirmationToken);
            }

            if (user.EmailConfirmed)
            {
                return Result.Failure(AuthErrors.InvalidEmailConfirmationToken);
            }

            IdentityResult identityResult = await _userManager.ConfirmEmailAsync(user, command.Token);

            if (identityResult.Succeeded)
            {
                return Result.Success();
            }

            if (identityResult.Errors.Any(e => e.Code is "InvalidToken"))
            {
                return Result.Failure(AuthErrors.InvalidEmailConfirmationToken);
            }

            string errors = string.Join(", ", identityResult.Errors.Select(e => e.Description));
            LogEmailConfirmationFailed(_logger, user.Id, errors);
            return Result.Failure(AuthErrors.EmailConfirmationFailed(errors));
        }

        [LoggerMessage(EventId = EventIds.EmailConfirmationFailed, Level = LogLevel.Warning, Message = "Email confirmation failed for user {UserId}. Errors: {Errors}")]
        private static partial void LogEmailConfirmationFailed(ILogger logger, Guid userId, string errors);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/confirm-email", async (
                ConfirmEmailRequest request,
                ICommandHandler<Command, Result> handler,
                CancellationToken cancellationToken) =>
            {
                Command command = new(request.Email, request.Token);

                Result commandResult = await handler.Handle(command, cancellationToken);

                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.Ok();
            })
            .AllowAnonymous()
            .RequireRateLimiting("auth-endpoint-limit")
            .WithName(nameof(ConfirmEmail))
            .WithTags("Authentication")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
