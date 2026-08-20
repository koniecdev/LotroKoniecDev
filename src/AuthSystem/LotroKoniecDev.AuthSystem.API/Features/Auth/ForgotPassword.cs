using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
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
        /// A hash computed up front, so the not-found path takes as long as the normal one.
        /// </summary>
        private static readonly string DummyPasswordHash =
            new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly AuthDbContext _db;
        private readonly OutboxWriter _outboxWriter;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            AuthDbContext db,
            OutboxWriter outboxWriter,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _db = db;
            _outboxWriter = outboxWriter;
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

            // Every path pays the same PBKDF2 cost. Running the dummy hash only when the user is not
            // found would make real accounts answer measurably faster, because their path is only a
            // cheap outbox insert, and the response time would then tell an attacker which accounts
            // exist (ADR-0038 decision 5).
            _ = _userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

            if (user is null)
            {
                string maskedEmail = command.Email.MaskEmail();
                LogPasswordResetNonExistent(_logger, maskedEmail);

                // Always report success, so nobody can find out which e-mails are registered.
                return Result.Success();
            }

            // No token is created here and the deletion window is not checked here. The payload holds
            // only the id, and the dispatch processor creates the token and does that check when it
            // sends (ADR-0038 decision 2).
            _outboxWriter.Enqueue(new PasswordResetRequested(user.Id));
            await _db.SaveChangesAsync(cancellationToken);

            _outboxWriter.NotifyEnqueuedCommitted();

            return Result.Success();
        }

        [LoggerMessage(EventId = EventIds.ForgotPasswordNonExistent, Level = LogLevel.Information, Message = "Password reset requested for non-existent email {Email}")]
        private static partial void LogPasswordResetNonExistent(ILogger logger, string email);
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
