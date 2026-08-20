using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class ResendEmailConfirmation : IApiEndpoint
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
        private readonly IAccountConfirmationEmailSender _accountConfirmationEmailSender;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IAccountConfirmationEmailSender accountConfirmationEmailSender,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _accountConfirmationEmailSender = accountConfirmationEmailSender;
            _validator = validator;
            _logger = logger;
        }

        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure(validationResult.ToValidationError(nameof(ResendEmailConfirmation)));
            }

            ApplicationUser? user = await _userManager.FindByEmailAsync(command.Email);

            string maskedEmail = command.Email.MaskEmail();

            if (user is null)
            {
                // Do the same work anyway, so the response time does not reveal whether the user
                // exists.
                _ = new PasswordHasher<ApplicationUser>()
                    .VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

                LogResendNonExistent(_logger, maskedEmail);

                // Always report success, so nobody can find out which e-mails are registered.
                return Result.Success();
            }

            if (user.EmailConfirmed)
            {
                LogResendAlreadyConfirmed(_logger, maskedEmail);

                // Always report success, so nobody can find out which e-mails are registered.
                return Result.Success();
            }

            string token = await _userManager.GenerateEmailConfirmationTokenAsync(user);

            // This send happens inside the request and skips the outbox on purpose (ADR-0038 decision
            // 4). Resend is the way out a user has when the pipeline itself is stuck, whether the
            // relay, the broker or the consumer, and ADR-0035 counts on it when it accepts a 6 hour
            // worst case for forgotten rows.
            // It is the only deliberate direct SMTP call on a request path. Do not move it to the
            // outbox: that would quietly break the reasoning in ADR-0035.
            Result emailResult = await _accountConfirmationEmailSender.SendEmailConfirmationAsync(
                user.Id, command.Email, token, cancellationToken);
            if (emailResult.IsFailure)
            {
                LogResendEmailFailed(_logger, user.Id, emailResult.Error.Message);
            }

            return Result.Success(); //anti-enumeration pattern
        }

        [LoggerMessage(EventId = EventIds.ResendConfirmNonExistent, Level = LogLevel.Information, Message = "Email confirmation resend requested for non-existent email {Email}")]
        private static partial void LogResendNonExistent(ILogger logger, string email);

        [LoggerMessage(EventId = EventIds.ResendConfirmAlreadyConfirmed, Level = LogLevel.Information, Message = "Email confirmation resend requested for already confirmed email {Email}")]
        private static partial void LogResendAlreadyConfirmed(ILogger logger, string email);

        [LoggerMessage(EventId = EventIds.ResendConfirmEmailFailed, Level = LogLevel.Error, Message = "Failed to send confirmation email for user {UserId}: {Error}")]
        private static partial void LogResendEmailFailed(ILogger logger, Guid userId, string error);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/resend-email-confirmation", async (
                ResendEmailConfirmationRequest request,
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
            .RequireRateLimiting("resend-confirmation-limit")
            .WithName(nameof(ResendEmailConfirmation))
            .WithTags("Authentication")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
