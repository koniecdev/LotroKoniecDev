using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class RegisterUser : IApiEndpoint
{
    internal sealed record Command(
        string Username,
        string Email,
        string Password,
        bool AcceptedPrivacyPolicy,
        bool AcceptedDataProcessingConsent) : ICommand<Result<IdentityId>>;

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        private const int UsernameMaxLength = 150;

        public CommandValidator()
        {
            RuleFor(x => x.Email)
                .NotEmpty().WithMessage("A valid email address is required.")
                .MaximumLength(EmailConstants.MaxLength)
                    .WithMessage($"Email must not exceed {EmailConstants.MaxLength} characters.")
                .Matches(EmailConstants.RegexPattern)
                    .WithMessage("A valid email address is required.");

            RuleFor(x => x.Username)
                .NotEmpty().WithMessage("Username is required.")
                .MaximumLength(UsernameMaxLength)
                    .WithMessage($"Username must not exceed {UsernameMaxLength} characters.");

            RuleFor(x => x.Password).ApplyPasswordRules();

            RuleFor(x => x.AcceptedPrivacyPolicy)
                .Equal(true).WithMessage("You must accept the privacy policy to register.");

            RuleFor(x => x.AcceptedDataProcessingConsent)
                .Equal(true).WithMessage("You must consent to data processing to register.");
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result<IdentityId>>
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAccountConfirmationEmailSender _accountConfirmationEmailSender;
        private readonly TimeProvider _timeProvider;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IAccountConfirmationEmailSender accountConfirmationEmailSender,
            TimeProvider timeProvider,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _accountConfirmationEmailSender = accountConfirmationEmailSender;
            _timeProvider = timeProvider;
            _validator = validator;
            _logger = logger;
        }

        public async ValueTask<Result<IdentityId>> Handle(
            Command command,
            CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure<IdentityId>(validationResult.ToValidationError(nameof(RegisterUser)));
            }

            ApplicationUser user;

            try
            {
                ApplicationUser? existingUser = await _userManager.FindByEmailAsync(command.Email);
                if (existingUser is not null)
                {
                    return Result.Failure<IdentityId>(AuthErrors.UserAlreadyExistsByEmail);
                }

                existingUser = await _userManager.FindByNameAsync(command.Username);
                if (existingUser is not null)
                {
                    return Result.Failure<IdentityId>(AuthErrors.UserAlreadyExistsByUsername);
                }

                user = new()
                {
                    UserName = command.Username,
                    Email = command.Email,
                    DataProcessingConsentGiven = command.AcceptedDataProcessingConsent,
                    DataProcessingConsentDate = command.AcceptedDataProcessingConsent ? _timeProvider.GetUtcNow() : null,
                    PrivacyPolicyAccepted = command.AcceptedPrivacyPolicy,
                    PrivacyPolicyAcceptedDate = command.AcceptedPrivacyPolicy ? _timeProvider.GetUtcNow() : null
                };

                IdentityResult result = await _userManager.CreateAsync(user, command.Password);

                if (!result.Succeeded)
                {
                    if (result.Errors.Any(e => e.Code is "DuplicateEmail"))
                    {
                        return Result.Failure<IdentityId>(AuthErrors.UserAlreadyExistsByEmail);
                    }

                    if (result.Errors.Any(e => e.Code is "DuplicateUserName"))
                    {
                        return Result.Failure<IdentityId>(AuthErrors.UserAlreadyExistsByUsername);
                    }

                    string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    return Result.Failure<IdentityId>(AuthErrors.RegistrationFailed(errors));
                }

                IdentityResult roleIdentityResult = await _userManager
                    .AddToRoleAsync(user, AuthConstants.Roles.Translator);
                if (!roleIdentityResult.Succeeded)
                {
                    return Result.Failure<IdentityId>(AuthErrors.RegistrationFailed(
                        string.Join(", ", roleIdentityResult.Errors.Select(e => e.Description))));
                }

                string emailToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);
                Result emailResult = await _accountConfirmationEmailSender.SendEmailConfirmationAsync(
                    command.Email, emailToken, cancellationToken);
                if (emailResult.IsFailure)
                {
                    string maskedEmail = command.Email.MaskEmail();
                    LogConfirmationEmailFailed(_logger, maskedEmail, emailResult.Error.Message);

                    await _userManager.ConfirmEmailAsync(user, emailToken);
                }
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                // DbUpdateException: unique constraint race condition on CreateAsync.
                // InvalidOperationException: "Sequence contains more than one element" from
                //   FindByEmailAsync when duplicate users exist concurrently — can happen in
                //   pre-checks or ConfirmEmailAsync's internal validation.
                string maskedEmail = command.Email.MaskEmail();
                LogConcurrentRegistration(_logger, ex, maskedEmail);
                return Result.Failure<IdentityId>(AuthErrors.UserAlreadyExistsByEmail);
            }

            // No cross-context profile creation here (the KittySaver RegisterUser->CreatePerson
            // saga is deliberately not lifted): the translator profile is provisioned lazily and
            // idempotently on the first authenticated TranslationSystem request (ADR-0002 §7).
            return IdentityId.Create(user.Id);
        }

        [LoggerMessage(EventId = EventIds.RegisterEmailFallback, Level = LogLevel.Warning, Message = "Failed to send confirmation email to {Email}: {Error}. Auto-confirming account as fallback")]
        private static partial void LogConfirmationEmailFailed(ILogger logger, string email, string error);

        [LoggerMessage(EventId = EventIds.RegisterConcurrentRace, Level = LogLevel.Warning, Message = "Concurrent registration race condition for email {Email}")]
        private static partial void LogConcurrentRegistration(ILogger logger, Exception exception, string email);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/register", async (
                RegisterRequest request,
                ICommandHandler<Command, Result<IdentityId>> handler,
                CancellationToken cancellationToken) =>
            {
                Command command = new(
                    request.Username,
                    request.Email,
                    request.Password,
                    request.AcceptedPrivacyPolicy,
                    request.AcceptedDataProcessingConsent);

                Result<IdentityId> commandResult = await handler.Handle(command, cancellationToken);

                // No GET-by-id endpoint exists for users (by design — user identity is only
                // accessible via OpenIddict's /connect/userinfo once the user has authenticated),
                // so no Location header is emitted. Clients read the newly-minted IdentityId
                // from the 201 response body.
                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.Json(commandResult.Value, statusCode: StatusCodes.Status201Created);
            })
            .AllowAnonymous()
            .RequireRateLimiting("auth-endpoint-limit")
            .WithName("RegisterUser")
            .WithTags("Authentication")
            .Produces<IdentityId>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
