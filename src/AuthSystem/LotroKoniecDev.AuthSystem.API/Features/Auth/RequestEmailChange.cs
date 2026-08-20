using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Starts an e-mail change. The address only moves when the link sent to the new mailbox is used, so
/// nothing on the account changes here (ADR-0048).
/// </summary>
/// <remarks>
/// Because there is no change to the user row, this handler has to save the outbox row itself:
/// <c>OutboxWriter.Enqueue</c> only adds it to the unit of work. <see cref="ForgotPassword"/> is the
/// shape to copy here, not <see cref="DeleteAccount"/>, which gets its save for free from
/// <c>UserManager.UpdateAsync</c>.
/// </remarks>
internal sealed partial class RequestEmailChange : IApiEndpoint
{
    internal sealed record Command(
        string UserId,
        string NewEmail,
        string CurrentPassword,
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

            RuleFor(x => x.CurrentPassword)
                .NotEmpty().WithMessage("Current password is required.");
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result>
    {
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
                return Result.Failure(validationResult.ToValidationError(nameof(RequestEmailChange)));
            }

            ApplicationUser? user = await _userManager.FindByIdAsync(command.UserId);
            if (user is null)
            {
                return Result.Failure(AuthErrors.UserNotFound);
            }

            string newEmail = command.NewEmail;

            // The account is locked for the whole grace period and the cancel link in the e-mail is the
            // only way back into it. Rotating the security stamp later in this flow would break that
            // link, so the change is refused while a deletion is pending (ADR-0031).
            if (user.DeletionScheduledAt is not null)
            {
                return Result.Failure(AuthErrors.DeletionAlreadyScheduled);
            }

            bool passwordValid = await _userManager.CheckPasswordAsync(user, command.CurrentPassword);
            if (!passwordValid)
            {
                return Result.Failure(AuthErrors.InvalidCurrentPassword);
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return Result.Failure(AuthErrors.UserNotFound);
            }

            string currentEmail = user.Email;

            if (string.Equals(
                    _userManager.NormalizeEmail(newEmail),
                    _userManager.NormalizeEmail(currentEmail),
                    StringComparison.Ordinal))
            {
                LogSameAddress(_logger, user.Id);
                return Result.Failure(AuthErrors.EmailChangeSameAddress);
            }

            // Registration already tells an anonymous caller that an address is taken, so saying it to
            // a caller who just proved they know this account's password reveals nothing new.
            ApplicationUser? addressOwner = await _userManager.FindByEmailAsync(newEmail);
            if (addressOwner is not null)
            {
                LogAddressTaken(_logger, user.Id, newEmail.MaskEmail());
                return Result.Failure(AuthErrors.UserAlreadyExistsByEmail);
            }

            _outboxWriter.Enqueue(new EmailChangeRequested(user.Id, currentEmail, newEmail));
            await _db.SaveChangesAsync(cancellationToken);

            // Only after the commit. The relay reads committed rows, so a signal sent earlier could
            // arrive while there is still nothing to see (ADR-0035).
            _outboxWriter.NotifyEnqueuedCommitted();

            LogChangeRequested(
                _logger, user.Id, currentEmail.MaskEmail(), newEmail.MaskEmail(), command.IpAddress, command.UserAgent);

            return Result.Success();
        }

        [LoggerMessage(EventId = EventIds.EmailChangeRequested, Level = LogLevel.Information, Message = "E-mail change requested for user {UserId}: {CurrentEmail} -> {NewEmail}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogChangeRequested(ILogger logger, Guid userId, string currentEmail, string newEmail, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.EmailChangeSameAddress, Level = LogLevel.Information, Message = "E-mail change refused for user {UserId}: the new address is the current one")]
        private static partial void LogSameAddress(ILogger logger, Guid userId);

        [LoggerMessage(EventId = EventIds.EmailChangeAddressTaken, Level = LogLevel.Information, Message = "E-mail change refused for user {UserId}: {NewEmail} belongs to another account")]
        private static partial void LogAddressTaken(ILogger logger, Guid userId, string newEmail);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/account/change-email", async (
                ChangeEmailRequest request,
                ClaimsPrincipal user,
                HttpContext httpContext,
                ICommandHandler<Command, Result> handler,
                CancellationToken cancellationToken) =>
            {
                string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? user.FindFirstValue(OpenIddictConstants.Claims.Subject);

                if (string.IsNullOrEmpty(userId))
                {
                    return Results.Unauthorized();
                }

                Command command = new(
                    userId,
                    // Trimmed before validation, not after: the address rule rejects any whitespace, so
                    // a trailing space from a paste would otherwise fail instead of being cleaned up.
                    request.NewEmail.Trim(),
                    request.CurrentPassword,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    httpContext.Request.Headers.UserAgent.ToString());

                Result commandResult = await handler.Handle(command, cancellationToken);

                return commandResult.IsFailure
                    ? Results.Problem(commandResult.Error.ToProblemDetails())
                    : Results.Ok();
            })
            .RequireAuthorization()
            .RequireRateLimiting("change-email-limit")
            .WithName(nameof(RequestEmailChange))
            .WithTags("Account")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
