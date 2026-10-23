using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
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
        /// So every path verifies exactly one hash.
        /// </summary>
        private static readonly string DummyPasswordHash =
            new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAccountConfirmationEmailSender _accountConfirmationEmailSender;
        private readonly IEmailConfirmationResendThrottle _throttle;
        private readonly IResponseTimeFloor _responseTimeFloor;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IAccountConfirmationEmailSender accountConfirmationEmailSender,
            IEmailConfirmationResendThrottle throttle,
            IResponseTimeFloor responseTimeFloor,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _accountConfirmationEmailSender = accountConfirmationEmailSender;
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
                return Result.Failure(validationResult.ToValidationError(nameof(ResendEmailConfirmation)));
            }

            // Only an unconfirmed account with a permit sends a live mail, so every answer waits for the
            // longer floor (ADR-0059).
            return await _responseTimeFloor.HoldAsync(
                ResponseTimeFloors.LiveMailSend,
                () => ResendAsync(command, cancellationToken));
        }

        private async Task<Result> ResendAsync(Command command, CancellationToken cancellationToken)
        {
            ApplicationUser? user = await _userManager.FindByEmailAsync(command.Email);

            // On every path, the second layer under the floor (ADR-0059 §5).
            _ = _userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, "DummyP@ssw0rd!");

            string maskedEmail = command.Email.MaskEmail();

            if (user is null)
            {
                LogResendNonExistent(_logger, maskedEmail);

                // Success on every branch, so nobody can learn which e-mails are registered.
                return Result.Success();
            }

            if (user.EmailConfirmed)
            {
                LogResendAlreadyConfirmed(_logger, maskedEmail);

                return Result.Success();
            }

            // Only where a mail would really go out, keyed on the account's inbox, so the page, the
            // endpoint and every spelling of that inbox share one budget (ADR-0057).
            if (!_throttle.TryAcquire(MailboxKey.FromNormalizedEmail(user.NormalizedEmail)))
            {
                LogResendThrottled(_logger, maskedEmail);
                return Result.Success();
            }

            string token = await _userManager.GenerateEmailConfirmationTokenAsync(user);

            // The only direct SMTP send on a request path, outside the outbox (ADR-0038 decision 4).
            // Resend is the way out when the pipeline is stuck, and ADR-0035 counts on it, so do not move
            // it to the outbox.
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

        [LoggerMessage(EventId = EventIds.ResendConfirmThrottled, Level = LogLevel.Warning, Message = "Email confirmation resend throttled for {Email}: the send budget of the inbox is spent")]
        private static partial void LogResendThrottled(ILogger logger, string email);

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
