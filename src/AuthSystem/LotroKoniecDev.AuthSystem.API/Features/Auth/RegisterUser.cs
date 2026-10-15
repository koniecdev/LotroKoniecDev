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

            // The password rules and the breach check run before the address is looked up, so a taken
            // and a free address get the same answer to a weak or leaked password, and both wait for the
            // same call to the breach service (#860). They run outside the transaction and the floor:
            // nothing in them depends on the address.
            IdentityResult passwordResult = await ValidatePasswordAsync(NewAccount(command), command.Password);
            if (!passwordResult.Succeeded)
            {
                return passwordResult.IsBreachedPassword
                    ? Result.Failure(AuthErrors.PasswordFoundInBreaches)
                    : Result.Failure(AuthErrors.RegistrationFailed(
                        string.Join(", ", passwordResult.Errors.Select(e => e.Description))));
            }

            // The one hash of every branch, made before the branches split: a taken or reserved address
            // pays it too, although no account is created (ADR-0059 §5).
            string passwordHash = _userManager.PasswordHasher.HashPassword(NewAccount(command), command.Password);

            // The context has EnableRetryOnFailure on, so EF refuses a transaction we start ourselves
            // unless the whole unit of work runs inside an execution strategy. A retry has to be able
            // to replay everything from begin to commit, not one statement inside it.
            IExecutionStrategy executionStrategy = _db.Database.CreateExecutionStrategy();
            RegistrationAttempt attempt = new();

            // A free address writes an account and a taken one only a notice, so every answer waits for
            // the floor (ADR-0059).
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
            // A retry starts after a rolled-back transaction, but the change tracker still holds the
            // previous attempt's entities as Added. Replaying without clearing it would insert twice.
            _db.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await _db.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // A known corner case: if the answer to CommitAsync is lost after the server really
                // committed, the execution strategy replays, finds its own new username below, and
                // answers "username taken" for a registration that in fact worked. A taken address
                // replays into a second notice instead. We accept both. The committed outbox row still
                // sends the confirmation, so the user can finish the flow, and a watertight fix with
                // ExecuteInTransaction and a verification query would buy little.

                // The username first. It is a public handle, so "taken" tells nothing, but checked after
                // the address it would: a known taken username paired with any address would answer
                // "username taken" for a free address and "check your inbox" for a taken one (#860).
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

                // An address freed by an e-mail change is not free while its owner still holds a
                // working undo link (#684). Occupying the row is all it takes to make that link fail
                // forever, and the row is occupied by whoever types the address in here: CreateAsync
                // writes it before anything is confirmed, RequireConfirmedEmail only blocks login, and
                // no job ever removes an unconfirmed registration. It answers like any other address and
                // mails nobody: the address has no account to tell. It still spends a permit, so a spent
                // budget answers the same here too.
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

                // The overload without a password, because the password is checked and hashed above.
                // The one with a password would run the validators again, the breach check included.
                IdentityResult result = await _userManager.CreateAsync(user);

                if (!result.Succeeded)
                {
                    if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateUserName)))
                    {
                        return Result.Failure(AuthErrors.UserAlreadyExistsByUsername);
                    }

                    // Identity looks the address up once more before it writes, so a registration that
                    // started at the same moment can take it in between. That answers as a taken address
                    // does, without the notice: the address got its account a moment ago (#860). It must
                    // never reach the general message below, whose text would name the taken address.
                    if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail)))
                    {
                        LogConcurrentRegistration(_logger, null, command.Email.MaskEmail());
                        return Result.Success();
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

                // The last check before the mail is queued, so a registration refused above spends nothing.
                // Leaving here without a commit rolls the new account back.
                if (!TryTakeMailboxPermit(command, attempt))
                {
                    return Result.Failure(AuthErrors.RegistrationMailboxThrottled);
                }

                _outboxWriter.Enqueue(new EmailConfirmationRequested(user.Id));
                await _db.SaveChangesAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                // Only after the commit. The relay reads committed rows, so a signal sent inside the
                // transaction could arrive while there is still nothing to see (ADR-0035).
                _outboxWriter.NotifyEnqueuedCommitted();

                // No profile is created in the other context here, on purpose: the translator profile is
                // created on the first authenticated TranslationSystem request, and creating it twice
                // is safe (ADR-0002 §7).
                LogRegistered(_logger, user.Id);
                return Result.Success();
            }
            // Any other save error goes up to the execution strategy, which replays a transient one (#845).
            catch (DbUpdateException ex) when (ex.TakenAccountValueError(_db.Model) is { } lostRaceError)
            {
                LogConcurrentRegistration(_logger, ex, command.Email.MaskEmail());

                // A lost race for the address answers as a taken address does (#860). The username is a
                // public handle, so its race may still say "taken".
                return lostRaceError == AuthErrors.UserAlreadyExistsByEmail
                    ? Result.Success()
                    : Result.Failure(lostRaceError);
            }
        }

        /// <summary>
        /// The answer for an address that already has an account: the same as for a free one, and a notice
        /// to the owner instead of a new account (#860). The notice comes from the same per-inbox budget as
        /// the confirmation mail, so a spent budget answers the same on both branches (ADR-0057).
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

            _outboxWriter.Enqueue(new RegistrationAttemptedOnExistingAccount(addressOwner.Id));
            await _db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            _outboxWriter.NotifyEnqueuedCommitted();

            LogAddressOwnerNotified(_logger, addressOwner.Id);
            return Result.Success();
        }

        /// <summary>
        /// One permit per registration, whichever branch it takes. A replay after a transient database
        /// error must not take a second one (ADR-0057). The key comes from the typed address on every
        /// branch, so a free and a taken address count in the same budget.
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
        /// Every password validator, as <c>UserManager.CreateAsync(user, password)</c> would run them.
        /// <c>UserManager.ValidatePasswordAsync</c>, which does this, is protected, so the loop is written
        /// out here.
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

                // 202 with no body for a new account and for a taken address alike: a new account's id
                // would be the one thing that tells them apart (#860).
                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.Accepted();
            })
            .AllowAnonymous()
            // Its own policy, keyed on the connection's address: the frontend never calls this endpoint,
            // and a leaked frontend key must not buy a fresh confirmation-mail budget per invented
            // address (ADR-0054 §3).
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
