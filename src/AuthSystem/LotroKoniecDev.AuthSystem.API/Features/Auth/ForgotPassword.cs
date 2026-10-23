using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
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
        /// Lets every path verify exactly one hash.
        /// </summary>
        private static readonly string DummyPasswordHash =
            new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly AuthDbContext _db;
        private readonly OutboxWriter _outboxWriter;
        private readonly IPasswordResetRequestThrottle _throttle;
        private readonly IResponseTimeFloor _responseTimeFloor;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            AuthDbContext db,
            OutboxWriter outboxWriter,
            IPasswordResetRequestThrottle throttle,
            IResponseTimeFloor responseTimeFloor,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _db = db;
            _outboxWriter = outboxWriter;
            _throttle = throttle;
            _responseTimeFloor = responseTimeFloor;
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

            // Only a real account with a permit writes an outbox row (ADR-0059).
            return await _responseTimeFloor.HoldAsync(
                ResponseTimeFloors.AccountLookup,
                () => RequestResetAsync(command, cancellationToken));
        }

        private async Task<Result> RequestResetAsync(Command command, CancellationToken cancellationToken)
        {
            ApplicationUser? user = await _userManager.FindByEmailAsync(command.Email);

            // On every path: a real account's path is only a cheap outbox insert, so a dummy hash only for
            // unknown addresses would make real accounts answer faster. The second layer under the floor
            // (ADR-0059 §5).
            _ = _userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

            if (user is null)
            {
                string maskedEmail = command.Email.MaskEmail();
                LogPasswordResetNonExistent(_logger, maskedEmail);

                // Success on every branch, so nobody can learn which e-mails are registered.
                return Result.Success();
            }

            // The page's budget too: this endpoint has no caller in the product, but it is public, so it
            // must not be the way around the budget.
            if (!_throttle.TryAcquire(user))
            {
                LogPasswordResetThrottled(_logger, command.Email.MaskEmail());
                return Result.Success();
            }

            // The dispatch processor creates the token and checks the deletion window when it sends
            // (ADR-0038 decision 2).
            _outboxWriter.Enqueue(new PasswordResetRequested(user.Id));
            await _db.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }

        [LoggerMessage(EventId = EventIds.ForgotPasswordNonExistent, Level = LogLevel.Information, Message = "Password reset requested for non-existent email {Email}")]
        private static partial void LogPasswordResetNonExistent(ILogger logger, string email);

        [LoggerMessage(EventId = EventIds.PasswordResetRequestThrottled, Level = LogLevel.Warning, Message = "Password reset request throttled for {Email}: the send budget is spent")]
        private static partial void LogPasswordResetThrottled(ILogger logger, string email);
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
