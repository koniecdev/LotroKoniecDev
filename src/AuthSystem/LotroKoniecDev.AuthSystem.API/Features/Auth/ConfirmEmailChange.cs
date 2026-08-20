using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Applies an e-mail change once the owner of the new mailbox follows the link. The address,
/// <see cref="ApplicationUser.EmailConfirmed"/> and the new security stamp land in one save, together
/// with the outbox row that notifies both mailboxes and arms the revert link (ADR-0048).
/// </summary>
/// <remarks>
/// The single save is not a style choice. Identity runs with <c>RequireConfirmedEmail</c>, so an
/// address that lands without its confirmation flag would lock the user out of their own account.
/// The token is checked before anything is enqueued, which is why this flow uses its own token
/// provider instead of <c>UserManager.ChangeEmailAsync</c>.
/// </remarks>
internal sealed partial class ConfirmEmailChange
{
    internal sealed record Command(
        string UserId,
        string NewEmail,
        string Token,
        string? IpAddress,
        string? UserAgent) : ICommand<Result>;

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(x => x.UserId)
                .NotEmpty().WithMessage("User ID is required.");

            RuleFor(x => x.NewEmail)
                .NotEmpty().WithMessage("A valid email address is required.")
                .MaximumLength(EmailConstants.MaxLength)
                    .WithMessage($"Email must not exceed {EmailConstants.MaxLength} characters.")
                .Matches(EmailConstants.RegexPattern)
                    .WithMessage("A valid email address is required.");

            RuleFor(x => x.Token)
                .NotEmpty().WithMessage("Confirmation token is required.");
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result>
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly AuthDbContext _db;
        private readonly OutboxWriter _outboxWriter;
        private readonly IUserSessionRevoker _sessionRevoker;
        private readonly IValidator<Command> _validator;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            AuthDbContext db,
            OutboxWriter outboxWriter,
            IUserSessionRevoker sessionRevoker,
            IValidator<Command> validator,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _db = db;
            _outboxWriter = outboxWriter;
            _sessionRevoker = sessionRevoker;
            _validator = validator;
            _logger = logger;
        }

        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                return Result.Failure(AuthErrors.InvalidEmailChangeToken);
            }

            string newEmail = command.NewEmail.Trim();

            ApplicationUser? user = await _userManager.FindByIdAsync(command.UserId);

            // An unknown user, a tampered address and an expired token all answer the same way, so the
            // page cannot be used to learn anything about an account.
            if (user is null)
            {
                LogTokenInvalid(_logger, newEmail.MaskEmail(), command.IpAddress, command.UserAgent);
                return Result.Failure(AuthErrors.InvalidEmailChangeToken);
            }

            if (user.DeletionScheduledAt is not null)
            {
                return Result.Failure(AuthErrors.DeletionAlreadyScheduled);
            }

            // The target address is baked into the purpose, so editing it in the link fails here.
            bool tokenValid = await _userManager.VerifyUserTokenAsync(
                user,
                EmailChangeTokenProvider.ProviderName,
                EmailChangeTokenProvider.PurposeFor(newEmail),
                command.Token);

            if (!tokenValid)
            {
                LogTokenInvalid(_logger, newEmail.MaskEmail(), command.IpAddress, command.UserAgent);
                return Result.Failure(AuthErrors.InvalidEmailChangeToken);
            }

            ApplicationUser? addressOwner = await _userManager.FindByEmailAsync(newEmail);
            if (addressOwner is not null)
            {
                return Result.Failure(AuthErrors.UserAlreadyExistsByEmail);
            }

            string previousEmail = user.Email ?? string.Empty;

            user.Email = newEmail;

            // The address and its confirmation flag have to move together. NormalizedEmail is left
            // alone on purpose: UpdateAsync recomputes it after validation and before the write, so
            // setting it here would only be overwritten.
            user.EmailConfirmed = true;

            // A new stamp ends every session and makes this link single-use. It is set inside the same
            // save that commits the outbox row (ADR-0038 decision 2), so the revert token the dispatch
            // processor creates moments later is built from the stamp that was actually stored.
            user.SecurityStamp = Guid.NewGuid().ToString();

            _outboxWriter.Enqueue(new EmailChangeCompleted(user.Id, previousEmail, newEmail));

            Result<IdentityResult> updateResult = await TryUpdateAsync(user);
            if (updateResult.IsFailure)
            {
                DiscardPendingOutboxMessages();
                return Result.Failure(updateResult.Error);
            }

            if (!updateResult.Value.Succeeded)
            {
                DiscardPendingOutboxMessages();

                string errors = string.Join(", ", updateResult.Value.Errors.Select(e => e.Description));
                LogUpdateFailed(_logger, user.Id, errors);
                return Result.Failure(AuthErrors.EmailChangeFailed(errors));
            }

            _outboxWriter.NotifyEnqueuedCommitted();

            await _sessionRevoker.RevokeAllAsync(user.Id.ToString(), cancellationToken);

            LogChangeApplied(
                _logger, user.Id, previousEmail.MaskEmail(), newEmail.MaskEmail(), command.IpAddress, command.UserAgent);

            return Result.Success();
        }

        /// <summary>
        /// The uniqueness check above is an ordinary query, so two requests can pass it at the same
        /// time. The unique index is what really decides, and <c>UserStore.UpdateAsync</c> only handles
        /// concurrency conflicts, so the duplicate-key error arrives here as a raw
        /// <see cref="DbUpdateException"/>. It is caught for the same reason registration catches it:
        /// a person who followed a link from their inbox must see an error page, not a crash.
        /// </summary>
        private async Task<Result<IdentityResult>> TryUpdateAsync(ApplicationUser user)
        {
            try
            {
                return Result.Success(await _userManager.UpdateAsync(user));
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                LogUpdateRace(_logger, ex, user.Id);
                return Result.Failure<IdentityResult>(AuthErrors.UserAlreadyExistsByEmail);
            }
        }

        /// <summary>
        /// A failed update never reaches the store, so the outbox row it was meant to travel with is
        /// still sitting in the change tracker. Detaching it keeps a later save in this same request
        /// from announcing a change that did not happen.
        /// </summary>
        private void DiscardPendingOutboxMessages()
        {
            foreach (EntityEntry<OutboxMessage> entry in
                     _db.ChangeTracker.Entries<OutboxMessage>().Where(e => e.State is EntityState.Added))
            {
                entry.State = EntityState.Detached;
            }
        }

        [LoggerMessage(EventId = EventIds.EmailChangeTokenInvalid, Level = LogLevel.Warning, Message = "Invalid e-mail change token presented for {NewEmail}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogTokenInvalid(ILogger logger, string newEmail, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.EmailChangeApplied, Level = LogLevel.Information, Message = "E-mail change applied for user {UserId}: {PreviousEmail} -> {NewEmail}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogChangeApplied(ILogger logger, Guid userId, string previousEmail, string newEmail, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.EmailChangeUpdateFailed, Level = LogLevel.Error, Message = "Failed to persist the e-mail change for user {UserId}: {Errors}")]
        private static partial void LogUpdateFailed(ILogger logger, Guid userId, string errors);

        [LoggerMessage(EventId = EventIds.EmailChangeConfirmRace, Level = LogLevel.Warning, Message = "E-mail change for user {UserId} lost a race for the new address")]
        private static partial void LogUpdateRace(ILogger logger, Exception exception, Guid userId);
    }
}
