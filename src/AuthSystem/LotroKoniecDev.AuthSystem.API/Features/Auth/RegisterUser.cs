using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using Microsoft.EntityFrameworkCore.Storage;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class RegisterUser : IApiEndpoint
{
    internal sealed record Command(
        string Username,
        string Email,
        string Password,
        bool AcceptedPrivacyPolicy,
        bool AcceptedTermsOfService) : ICommand<Result>;

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

            RuleFor(x => x.AcceptedTermsOfService)
                .Equal(true).WithMessage("You must accept the terms of service to register.");
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result>
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly TimeProvider _timeProvider;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;
        private readonly AuthDbContext _db;
        private readonly OutboxWriter _outboxWriter;
        private readonly IEmailChangeRevertReservation _revertReservation;
        private readonly IRegistrationMailboxThrottle _mailboxThrottle;
        private readonly IResponseTimeFloor _responseTimeFloor;

        public Handler(
            UserManager<ApplicationUser> userManager,
            TimeProvider timeProvider,
            IValidator<Command> validator,
            ILogger<Handler> logger,
            AuthDbContext db,
            OutboxWriter outboxWriter,
            IEmailChangeRevertReservation revertReservation,
            IRegistrationMailboxThrottle mailboxThrottle,
            IResponseTimeFloor responseTimeFloor)
        {
            _userManager = userManager;
            _timeProvider = timeProvider;
            _validator = validator;
            _logger = logger;
            _db = db;
            _outboxWriter = outboxWriter;
            _revertReservation = revertReservation;
            _mailboxThrottle = mailboxThrottle;
            _responseTimeFloor = responseTimeFloor;
        }

        public async ValueTask<Result> Handle(
            Command command,
            CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure(validationResult.ToValidationError(nameof(RegisterUser)));
            }

            // Before the address lookup, so a taken and a free address get the same answer to a weak or
            // leaked password and wait for the same breach call (#860).
            ApplicationUser candidate = NewAccount(command);
            IdentityResult passwordResult = await ValidatePasswordAsync(candidate, command.Password);
            if (!passwordResult.Succeeded)
            {
                return passwordResult.IsBreachedPassword
                    ? Result.Failure(AuthErrors.PasswordFoundInBreaches)
                    : Result.Failure(AuthErrors.RegistrationFailed(
                        string.Join(", ", passwordResult.Errors.Select(e => e.Description))));
            }

            // Before the branches split, so a taken or reserved address pays for the hash too (ADR-0059 §5).
            string passwordHash = _userManager.PasswordHasher.HashPassword(candidate, command.Password);

            // With EnableRetryOnFailure on, EF refuses our own transaction outside an execution strategy,
            // which replays everything from begin to commit.
            IExecutionStrategy executionStrategy = _db.Database.CreateExecutionStrategy();
            RegistrationAttempt attempt = new();

            // A free address writes an account and a taken one only a notice (ADR-0059).
            return await _responseTimeFloor.HoldAsync(
                ResponseTimeFloors.Registration,
                () => executionStrategy.ExecuteAsync(async () =>
                    await RegisterAsync(command, passwordHash, attempt, cancellationToken)));
        }

        private async Task<Result> RegisterAsync(
            Command command,
            string passwordHash,
            RegistrationAttempt attempt,
            CancellationToken cancellationToken)
        {
            // After a rollback the change tracker still holds the previous attempt's entities as Added.
            _db.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await _db.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // Accepted corner case: if CommitAsync's answer is lost after it landed, the replay answers
                // "username taken" (or sends a second notice). The committed outbox row still sends the
                // confirmation, so the user can finish.

                // The username first: checked after the address, a known taken username would answer
                // differently for a free and a taken address (#860).
                ApplicationUser? usernameOwner = await _userManager.FindByNameAsync(command.Username);
                if (usernameOwner is not null)
                {
                    return Result.Failure(AuthErrors.UserAlreadyExistsByUsername);
                }

                ApplicationUser? addressOwner = await _userManager.FindByEmailAsync(command.Email);
                if (addressOwner is not null)
                {
                    return await NotifyAddressOwnerAsync(addressOwner, command, attempt, transaction, cancellationToken);
                }

                // An address held for an undo link is not free (#684): an unconfirmed account on it would
                // break that link forever, and nothing removes unconfirmed accounts. It answers like any
                // other address and mails nobody, but still spends a permit, so the budget reveals nothing.
                bool reserved = await _revertReservation.IsReservedAsync(
                    command.Email, exceptUserId: null, cancellationToken);

                if (reserved)
                {
                    LogReservedAddressRefused(_logger, command.Email.MaskEmail());
                    return TryTakeMailboxPermit(command, attempt)
                        ? Result.Success()
                        : Result.Failure(AuthErrors.RegistrationMailboxThrottled);
                }

                ApplicationUser user = NewAccount(command);
                user.PasswordHash = passwordHash;

                // Without a password: the one with a password would run the validators and the breach
                // check again.
                IdentityResult result = await _userManager.CreateAsync(user);

                if (!result.Succeeded)
                {
                    if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateUserName)))
                    {
                        return Result.Failure(AuthErrors.UserAlreadyExistsByUsername);
                    }

                    // Identity looks the address up again before it writes, so a parallel registration
                    // can take it in between. That answers as a taken address does, without the notice
                    // (#860), and never reaches the message below, which would name the address.
                    if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail)))
                    {
                        LogConcurrentRegistration(_logger, null, command.Email.MaskEmail());
                        return AnswerLostAddressRace(command, attempt);
                    }

                    string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    return Result.Failure(AuthErrors.RegistrationFailed(errors));
                }

                IdentityResult roleIdentityResult = await _userManager
                    .AddToRoleAsync(user, AuthConstants.Roles.Translator);
                if (!roleIdentityResult.Succeeded)
                {
                    return Result.Failure(AuthErrors.RegistrationFailed(
                        string.Join(", ", roleIdentityResult.Errors.Select(e => e.Description))));
                }

                // Last, so a registration refused above spends nothing. Leaving without a commit rolls
                // the new account back.
                if (!TryTakeMailboxPermit(command, attempt))
                {
                    return Result.Failure(AuthErrors.RegistrationMailboxThrottled);
                }

                _outboxWriter.Enqueue(new EmailConfirmationRequested(user.Id));
                await _db.SaveChangesAsync(cancellationToken);

                // The commit, not the save, wakes the relay: it reads committed rows (ADR-0035).
                await transaction.CommitAsync(cancellationToken);

                // The translator profile is created on the first authenticated TranslationSystem request
                // (ADR-0002 §7).
                LogRegistered(_logger, user.Id);
                return Result.Success();
            }
            // Any other save error goes up to the execution strategy (#845).
            catch (DbUpdateException ex) when (ex.TakenAccountValueError(_db.Model) is { } lostRaceError)
            {
                LogConcurrentRegistration(_logger, ex, command.Email.MaskEmail());

                // A lost address race answers as a taken address does (#860); a username is public.
                return lostRaceError == AuthErrors.UserAlreadyExistsByEmail
                    ? AnswerLostAddressRace(command, attempt)
                    : Result.Failure(lostRaceError);
            }
        }

        /// <summary>
        /// The same answer as for a free address, and a notice to the owner instead of a new account (#860).
        /// The notice spends the same per-inbox budget as the confirmation mail (ADR-0057).
        /// </summary>
        private async Task<Result> NotifyAddressOwnerAsync(
            ApplicationUser addressOwner,
            Command command,
            RegistrationAttempt attempt,
            IDbContextTransaction transaction,
            CancellationToken cancellationToken)
        {
            if (!TryTakeMailboxPermit(command, attempt))
            {
                return Result.Failure(AuthErrors.RegistrationMailboxThrottled);
            }

            _outboxWriter.Enqueue(new RegistrationAttemptedOnExistingAccount(addressOwner.Id, command.Email));
            await _db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            LogAddressOwnerNotified(_logger, addressOwner.Id);
            return Result.Success();
        }

        /// <summary>
        /// Spends a permit like a taken address, so the budget does not show the address was free a moment
        /// ago. It sends nothing: the winner's confirmation already goes to that inbox.
        /// </summary>
        private Result AnswerLostAddressRace(Command command, RegistrationAttempt attempt) =>
            TryTakeMailboxPermit(command, attempt)
                ? Result.Success()
                : Result.Failure(AuthErrors.RegistrationMailboxThrottled);

        /// <summary>
        /// One permit per registration, whichever branch it takes, and none more on a replay (ADR-0057).
        /// </summary>
        private bool TryTakeMailboxPermit(Command command, RegistrationAttempt attempt)
        {
            if (attempt.MailboxPermitTaken)
            {
                return true;
            }

            if (!_mailboxThrottle.TryAcquire(MailboxKey.FromNormalizedEmail(_userManager.NormalizeEmail(command.Email))))
            {
                LogMailboxThrottled(_logger, command.Email.MaskEmail());
                return false;
            }

            attempt.MailboxPermitTaken = true;
            return true;
        }

        /// <summary>
        /// Written out because <c>UserManager.ValidatePasswordAsync</c> is protected.
        /// </summary>
        private async Task<IdentityResult> ValidatePasswordAsync(ApplicationUser user, string password)
        {
            List<IdentityError> errors = [];
            bool succeeded = true;

            foreach (IPasswordValidator<ApplicationUser> validator in _userManager.PasswordValidators)
            {
                IdentityResult result = await validator.ValidateAsync(_userManager, user, password);
                if (!result.Succeeded)
                {
                    succeeded = false;
                    errors.AddRange(result.Errors);
                }
            }

            return succeeded ? IdentityResult.Success : IdentityResult.Failed([.. errors]);
        }

        private ApplicationUser NewAccount(Command command)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();

            return new ApplicationUser
            {
                UserName = command.Username,
                Email = command.Email,
                PrivacyPolicyAccepted = command.AcceptedPrivacyPolicy,
                PrivacyPolicyAcceptedDate = command.AcceptedPrivacyPolicy ? now : null,
                TermsOfServiceAccepted = command.AcceptedTermsOfService,
                TermsOfServiceAcceptedDate = command.AcceptedTermsOfService ? now : null
            };
        }

        [LoggerMessage(EventId = EventIds.RegisterConcurrentRace, Level = LogLevel.Warning, Message = "Concurrent registration race condition for email {Email}")]
        private static partial void LogConcurrentRegistration(ILogger logger, Exception? exception, string email);

        [LoggerMessage(EventId = EventIds.RegisterReservedAddressRefused, Level = LogLevel.Warning, Message = "Registration refused for {Email}: the address is still reserved as another account's e-mail-change undo target")]
        private static partial void LogReservedAddressRefused(ILogger logger, string email);

        [LoggerMessage(EventId = EventIds.RegisterMailboxThrottled, Level = LogLevel.Warning, Message = "Registration refused for {Email}: the registration budget of the inbox is spent")]
        private static partial void LogMailboxThrottled(ILogger logger, string email);

        [LoggerMessage(EventId = EventIds.RegisterCompleted, Level = LogLevel.Information, Message = "User {UserId} registered")]
        private static partial void LogRegistered(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.RegisterAddressOwnerNotified, Level = LogLevel.Information, Message = "Registration hit the address of user {UserId}: no account was created and the owner gets a notice")]
        private static partial void LogAddressOwnerNotified(ILogger logger, Guid userId);

        /// <summary>
        /// Kept across the replays of the transaction.
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
                ICommandHandler<Command, Result> handler,
                CancellationToken cancellationToken) =>
            {
                Command command = new(
                    request.Username,
                    request.Email,
                    request.Password,
                    request.AcceptedPrivacyPolicy,
                    request.AcceptedTermsOfService);

                Result commandResult = await handler.Handle(command, cancellationToken);

                // No body: a new account's id would tell a free address from a taken one (#860).
                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.Accepted();
            })
            .AllowAnonymous()
            // Keyed on the connection's address (ADR-0054 §3); Program.cs says why.
            .RequireRateLimiting("register-limit")
            .WithName("RegisterUser")
            .WithTags("Authentication")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
