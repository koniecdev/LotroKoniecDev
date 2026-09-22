using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Hands the GDPR Art. 15 export over, behind the current password (#690, ADR-0052). Every other
/// sensitive account action already asks for it, and this is the one that produces a single tidy file:
/// a bearer token alone must not be enough to take it away.
/// The matching GET is the account representation the account page renders, so it stays open to a
/// logged-in caller. The difference is deliberate and ADR-0052 holds the reasoning.
/// A query, not a command: nothing on the account changes. The password is checked and the attempt is
/// written to the audit log either way.
/// </summary>
internal sealed partial class DownloadAccountData : IApiEndpoint
{
    internal sealed record Query(
        string UserId,
        string Password,
        string? IpAddress,
        string? UserAgent) : IQuery<Result<AccountDataExportResponse>>;

    internal sealed partial class Handler : IQueryHandler<Query, Result<AccountDataExportResponse>>
    {
        /// <summary>Stands in for an address the account does not have, so a log line never reads as blank.</summary>
        private const string UnknownEmail = "***";

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IPasswordConfirmationThrottle _confirmationThrottle;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            IPasswordConfirmationThrottle confirmationThrottle,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _confirmationThrottle = confirmationThrottle;
            _logger = logger;
        }

        public async ValueTask<Result<AccountDataExportResponse>> Handle(
            Query query, CancellationToken cancellationToken)
        {
            // The account is found first, so that every attempt by a logged-in caller can be logged
            // against the account it named.
            ApplicationUser? user = await _userManager.FindByIdAsync(query.UserId);
            if (user is null)
            {
                // Reachable: an access token outlives the erasure of its account by a few minutes
                // (ADR-0049).
                LogExportRefusedForUnknownAccount(_logger, query.UserId, query.IpAddress, query.UserAgent);
                return Result.Failure<AccountDataExportResponse>(AuthErrors.UserNotFound);
            }

            string maskedEmail = string.IsNullOrWhiteSpace(user.Email)
                ? UnknownEmail
                : user.Email.MaskEmail();

            // A query validates inline, because FluentValidation is for commands only (house rule).
            if (string.IsNullOrWhiteSpace(query.Password))
            {
                LogExportRefused(_logger, user.Id, maskedEmail, "no password was sent", query.IpAddress, query.UserAgent);
                return Result.Failure<AccountDataExportResponse>(AuthErrors.ExportPasswordRequired);
            }

            // The permit is taken before the check, so a burst of guesses cannot slip past it (ADR-0053).
            if (!_confirmationThrottle.TryAcquire(user.Id))
            {
                LogExportRefused(_logger, user.Id, maskedEmail, "the password confirmation budget is spent", query.IpAddress, query.UserAgent);
                return Result.Failure<AccountDataExportResponse>(AuthErrors.PasswordConfirmationThrottled);
            }

            bool passwordValid = await _userManager.CheckPasswordAsync(user, query.Password);
            if (!passwordValid)
            {
                LogExportRefused(_logger, user.Id, maskedEmail, "the password did not match", query.IpAddress, query.UserAgent);
                return Result.Failure<AccountDataExportResponse>(AuthErrors.InvalidCurrentPassword);
            }

            AuthDataExportDto authData = await AccountDataExportReader.ReadExportAsync(_userManager, user);

            // For a browser download the IP and the user agent are the frontend's, not the reader's. The
            // frontend logs the real ones on its own route (ADR-0052).
            LogExportDownloaded(_logger, user.Id, maskedEmail, query.IpAddress, query.UserAgent);

            return Result.Success(new AccountDataExportResponse(authData, IsComplete: true));
        }

        [LoggerMessage(EventId = EventIds.ExportDataDownloaded, Level = LogLevel.Information, Message = "GDPR data export handed over for user {UserId} ({MaskedEmail}). IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogExportDownloaded(ILogger logger, Guid userId, string maskedEmail, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.ExportDataRefused, Level = LogLevel.Warning, Message = "GDPR data export refused for user {UserId} ({MaskedEmail}): {Reason}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogExportRefused(ILogger logger, Guid userId, string maskedEmail, string reason, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.ExportDataRefusedForUnknownAccount, Level = LogLevel.Warning, Message = "GDPR data export refused: the token names user {UserId}, and no such account exists. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogExportRefusedForUnknownAccount(ILogger logger, string userId, string? ipAddress, string? userAgent);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/account/data-export", async (
                DownloadAccountDataRequest request,
                ClaimsPrincipal user,
                HttpContext httpContext,
                IQueryHandler<Query, Result<AccountDataExportResponse>> handler,
                CancellationToken cancellationToken) =>
            {
                string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? user.FindFirstValue(OpenIddictConstants.Claims.Subject);

                if (string.IsNullOrEmpty(userId))
                {
                    return Results.Unauthorized();
                }

                Query query = new(
                    userId,
                    request.Password,
                    httpContext.Connection.RemoteIpAddress?.ToString(),
                    httpContext.Request.Headers.UserAgent.ToString());

                Result<AccountDataExportResponse> queryResult = await handler.Handle(query, cancellationToken);

                // No hypermedia links here. This response is a document the caller downloads, not a
                // resource they navigate from — the account representation is the GET, and ADR-0032
                // already drops the links when the frontend writes the file.
                return queryResult.IsSuccess
                    ? Results.Ok(queryResult.Value)
                    : Results.Problem(queryResult.Error.ToProblemDetails());
            })
            .RequireAuthorization()
            // Off the per-IP policies: the brake is the per-account budget in the handler (ADR-0053).
            .DisableRateLimiting()
            .WithName(nameof(DownloadAccountData))
            .WithTags("Account")
            .Produces<AccountDataExportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
