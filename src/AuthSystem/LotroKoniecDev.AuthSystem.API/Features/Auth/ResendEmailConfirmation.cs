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
        /// Pre-computed hash for timing-equalization when user is not found.
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
                // Perform dummy work to prevent timing-based user enumeration
                _ = new PasswordHasher<ApplicationUser>()
                    .VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

                LogResendNonExistent(_logger, maskedEmail);

                // Always return success to prevent email enumeration
                return Result.Success();
            }

            if (user.EmailConfirmed)
            {
                LogResendAlreadyConfirmed(_logger, maskedEmail);

                // Always return success to prevent email enumeration
                return Result.Success();
            }

            string token = await _userManager.GenerateEmailConfirmationTokenAsync(user);

            // Deliberately synchronous and outbox-independent (ADR-0038 decision 4): resend is the
            // user-driven escape hatch ADR-0035's 6 h orphan tail is priced on, so it must keep
            // delivering when the pipeline itself (relay, broker, consumer) is what is stuck. It is
            // the only intentional direct-SMTP call on a request path — do not migrate it to the
            // outbox; that would silently invalidate ADR-0035's pricing.
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
