using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using Microsoft.EntityFrameworkCore.Storage;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class RegisterUser : IApiEndpoint
{
    internal sealed record Command(
        string Username,
        string Email,
        string Password,
        bool AcceptedPrivacyPolicy,
        bool AcceptedDataProcessingConsent,
        bool AcceptedTermsOfService) : ICommand<Result<IdentityId>>;

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

            RuleFor(x => x.Username)
                .NotEmpty().WithMessage("Username is required.")
                .MaximumLength(UsernameConstants.MaxLength)
                    .WithMessage($"Username must not exceed {UsernameConstants.MaxLength} characters.")
                .Matches(UsernameConstants.RegexPattern)
                    .WithMessage("Username may contain only letters and digits, without spaces.");

            RuleFor(x => x.Password).ApplyPasswordRules();

            RuleFor(x => x.AcceptedPrivacyPolicy)
                .Equal(true).WithMessage("You must accept the privacy policy to register.");

            RuleFor(x => x.AcceptedDataProcessingConsent)
                .Equal(true).WithMessage("You must consent to data processing to register.");

            RuleFor(x => x.AcceptedTermsOfService)
                .Equal(true).WithMessage("You must accept the terms of service to register.");
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result<IdentityId>>
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly TimeProvider _timeProvider;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;
        private readonly AuthDbContext _db;

        public Handler(
            UserManager<ApplicationUser> userManager,
            TimeProvider timeProvider,
            IValidator<Command> validator,
            ILogger<Handler> logger,
            AuthDbContext db)
        {
            _userManager = userManager;
            _timeProvider = timeProvider;
            _validator = validator;
            _logger = logger;
            _db = db;
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

            // The context enables EnableRetryOnFailure, so EF refuses a user-initiated transaction
            // unless the whole unit of work runs inside an execution strategy — a retry has to be
            // able to replay begin-to-commit, not a single statement inside it.
            IExecutionStrategy executionStrategy = _db.Database.CreateExecutionStrategy();

            return await executionStrategy.ExecuteAsync(async () => await RegisterAsync(command, cancellationToken));
        }

        private async Task<Result<IdentityId>> RegisterAsync(
            Command command,
            CancellationToken cancellationToken)
        {
            // A retried attempt starts from a rolled-back transaction while the tracker still holds
            // the previous attempt's entities as Added — replaying without a reset would insert twice.
            _db.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await _db.Database.BeginTransactionAsync(cancellationToken);

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

                ApplicationUser user = new()
                {
                    UserName = command.Username,
                    Email = command.Email,
                    DataProcessingConsentGiven = command.AcceptedDataProcessingConsent,
                    DataProcessingConsentDate = command.AcceptedDataProcessingConsent ? _timeProvider.GetUtcNow() : null,
                    PrivacyPolicyAccepted = command.AcceptedPrivacyPolicy,
                    PrivacyPolicyAcceptedDate = command.AcceptedPrivacyPolicy ? _timeProvider.GetUtcNow() : null,
                    TermsOfServiceAccepted = command.AcceptedTermsOfService,
                    TermsOfServiceAcceptedDate = command.AcceptedTermsOfService ? _timeProvider.GetUtcNow() : null
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

                EmailConfirmationRequested emailConfirmationRequested = new(user.Id);
                OutboxMessage outboxMessage = OutboxMessage.Create(
                    type: nameof(EmailConfirmationRequested),
                    payload: JsonSerializer.Serialize(emailConfirmationRequested),
                    occurredOn: _timeProvider.GetUtcNow());

                _db.OutboxMessages.Add(outboxMessage);
                await _db.SaveChangesAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                // No cross-context profile creation here (the KittySaver RegisterUser->CreatePerson
                // saga is deliberately not lifted): the translator profile is provisioned lazily and
                // idempotently on the first authenticated TranslationSystem request (ADR-0002 §7).
                return IdentityId.Create(user.Id);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                // DbUpdateException: unique constraint race condition on CreateAsync.
                // InvalidOperationException: "Sequence contains more than one element" from
                //   FindByEmailAsync when duplicate users exist concurrently.
                string maskedEmail = command.Email.MaskEmail();
                LogConcurrentRegistration(_logger, ex, maskedEmail);
                return Result.Failure<IdentityId>(AuthErrors.UserAlreadyExistsByEmail);
            }
        }

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
                    request.AcceptedDataProcessingConsent,
                    request.AcceptedTermsOfService);

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
