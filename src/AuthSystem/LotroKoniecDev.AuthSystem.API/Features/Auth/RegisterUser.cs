using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
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
        private readonly OutboxWriter _outboxWriter;
        private readonly IEmailChangeRevertReservation _revertReservation;
        private readonly IRegistrationMailboxThrottle _mailboxThrottle;

        public Handler(
            UserManager<ApplicationUser> userManager,
            TimeProvider timeProvider,
            IValidator<Command> validator,
            ILogger<Handler> logger,
            AuthDbContext db,
            OutboxWriter outboxWriter,
            IEmailChangeRevertReservation revertReservation,
            IRegistrationMailboxThrottle mailboxThrottle)
        {
            _userManager = userManager;
            _timeProvider = timeProvider;
            _validator = validator;
            _logger = logger;
            _db = db;
            _outboxWriter = outboxWriter;
            _revertReservation = revertReservation;
            _mailboxThrottle = mailboxThrottle;
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

            // The context has EnableRetryOnFailure on, so EF refuses a transaction we start ourselves
            // unless the whole unit of work runs inside an execution strategy. A retry has to be able
            // to replay everything from begin to commit, not one statement inside it.
            IExecutionStrategy executionStrategy = _db.Database.CreateExecutionStrategy();
            RegistrationAttempt attempt = new();

            return await executionStrategy.ExecuteAsync(async () =>
                await RegisterAsync(command, attempt, cancellationToken));
        }

        private async Task<Result<IdentityId>> RegisterAsync(
            Command command,
            RegistrationAttempt attempt,
            CancellationToken cancellationToken)
        {
            // A retry starts after a rolled-back transaction, but the change tracker still holds the
            // previous attempt's entities as Added. Replaying without clearing it would insert twice.
            _db.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await _db.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // A known corner case: if the answer to CommitAsync is lost after the server really
                // committed, the execution strategy replays, this check finds the row, and we answer
                // "taken" for a registration that in fact worked. We accept that. The committed outbox
                // row still sends the confirmation e-mail, so the user can finish the flow, and a
                // watertight fix with ExecuteInTransaction and a verification query would buy little.
                ApplicationUser? existingUser = await _userManager.FindByEmailAsync(command.Email);
                if (existingUser is not null)
                {
                    return Result.Failure<IdentityId>(AuthErrors.UserAlreadyExistsByEmail);
                }

                // An address freed by an e-mail change is not free while its owner still holds a
                // working undo link (#684). Occupying the row is all it takes to make that link fail
                // forever, and the row is occupied by whoever types the address in here: CreateAsync
                // writes it before anything is confirmed, RequireConfirmedEmail only blocks login, and
                // no job ever removes an unconfirmed registration. The refusal is the one this
                // endpoint already gives a taken address, because the address really was taken a
                // moment ago.
                bool reserved = await _revertReservation.IsReservedAsync(
                    command.Email, exceptUserId: null, cancellationToken);

                if (reserved)
                {
                    LogReservedAddressRefused(_logger, command.Email.MaskEmail());
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

                // The last check before the mail is queued, so a registration refused above spends nothing.
                // Leaving here without a commit rolls the new account back. A replay after a transient
                // database error must not take a second permit for the same registration (ADR-0057).
                if (!attempt.MailboxPermitTaken)
                {
                    if (!_mailboxThrottle.TryAcquire(MailboxKey.FromNormalizedEmail(user.NormalizedEmail)))
                    {
                        LogMailboxThrottled(_logger, command.Email.MaskEmail());
                        return Result.Failure<IdentityId>(AuthErrors.RegistrationMailboxThrottled);
                    }

                    attempt.MailboxPermitTaken = true;
                }

                _outboxWriter.Enqueue(new EmailConfirmationRequested(user.Id));
                await _db.SaveChangesAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                // Only after the commit. The relay reads committed rows, so a signal sent inside the
                // transaction could arrive while there is still nothing to see (ADR-0035).
                _outboxWriter.NotifyEnqueuedCommitted();

                // No profile is created in the other context here. The KittySaver
                // RegisterUser to CreatePerson saga was left out on purpose: the translator profile is
                // created on the first authenticated TranslationSystem request, and creating it twice
                // is safe (ADR-0002 §7).
                return IdentityId.Create(user.Id);
            }
            catch (DbUpdateException ex) when (LostRaceError(ex) is { } lostRaceError)
            {
                string maskedEmail = command.Email.MaskEmail();
                LogConcurrentRegistration(_logger, ex, maskedEmail);
                return Result.Failure<IdentityId>(lostRaceError);
            }
        }

        /// <summary>
        /// Two requests can pass every check above at the same moment, and then the unique index decides.
        /// The loser gets the answer the checks would have given it. Any other save error is not a taken
        /// value: it goes up to the execution strategy, which replays a transient one (#845).
        /// </summary>
        private Error? LostRaceError(DbUpdateException exception)
        {
            if (exception.InnerException is not PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation)
            {
                return null;
            }

            IIndex? index = _db.Model.FindEntityType(typeof(ApplicationUser))?
                .GetIndexes()
                .FirstOrDefault(i => string.Equals(
                    i.GetDatabaseName(), violation.ConstraintName, StringComparison.Ordinal));

            // Only a single-column index proves that the one value is taken. An index over more columns
            // would clash on the combination, not on the address or the name alone.
            return index?.Properties switch
            {
                [{ Name: nameof(ApplicationUser.Email) or nameof(ApplicationUser.NormalizedEmail) }] =>
                    AuthErrors.UserAlreadyExistsByEmail,
                [{ Name: nameof(ApplicationUser.UserName) or nameof(ApplicationUser.NormalizedUserName) }] =>
                    AuthErrors.UserAlreadyExistsByUsername,
                _ => null
            };
        }

        [LoggerMessage(EventId = EventIds.RegisterConcurrentRace, Level = LogLevel.Warning, Message = "Concurrent registration race condition for email {Email}")]
        private static partial void LogConcurrentRegistration(ILogger logger, Exception exception, string email);

        [LoggerMessage(EventId = EventIds.RegisterReservedAddressRefused, Level = LogLevel.Warning, Message = "Registration refused for {Email}: the address is still reserved as another account's e-mail-change undo target")]
        private static partial void LogReservedAddressRefused(ILogger logger, string email);

        [LoggerMessage(EventId = EventIds.RegisterMailboxThrottled, Level = LogLevel.Warning, Message = "Registration refused for {Email}: the registration budget of the inbox is spent")]
        private static partial void LogMailboxThrottled(ILogger logger, string email);

        /// <summary>
        /// What one registration keeps across the replays of its transaction.
        /// </summary>
        private sealed class RegistrationAttempt
        {
            public bool MailboxPermitTaken { get; set; }
        }
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

                // There is no get-user-by-id endpoint on purpose: a user's identity is only readable
                // through OpenIddict's /connect/userinfo after they log in. So we send no Location
                // header, and clients read the new IdentityId from the 201 response body.
                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.Json(commandResult.Value, statusCode: StatusCodes.Status201Created);
            })
            .AllowAnonymous()
            // Its own policy, keyed on the connection's address: the frontend never calls this endpoint,
            // and a leaked frontend key must not buy a fresh confirmation-mail budget per invented
            // address (ADR-0054 §3).
            .RequireRateLimiting("register-limit")
            .WithName("RegisterUser")
            .WithTags("Authentication")
            .Produces<IdentityId>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
