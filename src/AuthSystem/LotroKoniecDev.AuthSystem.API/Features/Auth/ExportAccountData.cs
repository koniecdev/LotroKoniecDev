using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// The auth part of the GDPR Art. 15 export. It asks for the current password, like every other
/// sensitive account action, so a bearer token alone does not turn an account into a tidy data file
/// (#690). It is a POST because a GET cannot carry the password. Every attempt is logged with the
/// masked address, the IP and the user agent.
/// </summary>
internal sealed partial class ExportAccountData : IApiEndpoint
{
    internal sealed record Query(
        string UserId,
        string Password,
        string? IpAddress,
        string? UserAgent) : IQuery<Result<AccountDataExportResponse>>;

    internal sealed partial class Handler : IQueryHandler<Query, Result<AccountDataExportResponse>>
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<Handler> _logger;

        public Handler(
            UserManager<ApplicationUser> userManager,
            ILogger<Handler> logger)
        {
            _userManager = userManager;
            _logger = logger;
        }

        public async ValueTask<Result<AccountDataExportResponse>> Handle(
            Query query, CancellationToken cancellationToken)
        {
            ApplicationUser? appUser = await _userManager.FindByIdAsync(query.UserId);
            if (appUser is null)
            {
                LogGdprExportUnknownUser(_logger, query.UserId, query.IpAddress, query.UserAgent);
                return Result.Failure<AccountDataExportResponse>(AuthErrors.UserNotFound);
            }

            string maskedEmail = string.IsNullOrWhiteSpace(appUser.Email) ? "***" : appUser.Email.MaskEmail();

            if (string.IsNullOrWhiteSpace(query.Password))
            {
                LogGdprExportRefused(_logger, appUser.Id, maskedEmail, query.IpAddress, query.UserAgent);
                return Result.Failure<AccountDataExportResponse>(AuthErrors.ExportPasswordRequired);
            }

            bool passwordValid = await _userManager.CheckPasswordAsync(appUser, query.Password);
            if (!passwordValid)
            {
                LogGdprExportRefused(_logger, appUser.Id, maskedEmail, query.IpAddress, query.UserAgent);
                return Result.Failure<AccountDataExportResponse>(AuthErrors.InvalidCurrentPassword);
            }

            IList<string> roles = await _userManager.GetRolesAsync(appUser);

            AccountDataExportResponse response = new(
                new AuthDataExportDto(
                    appUser.Id,
                    appUser.UserName ?? string.Empty,
                    appUser.Email ?? string.Empty,
                    appUser.PhoneNumber,
                    appUser.EmailConfirmed,
                    roles.ToList(),
                    appUser.DataProcessingConsentGiven,
                    appUser.DataProcessingConsentDate,
                    appUser.PrivacyPolicyAccepted,
                    appUser.PrivacyPolicyAcceptedDate,
                    appUser.TermsOfServiceAccepted,
                    appUser.TermsOfServiceAcceptedDate,
                    appUser.DeletionScheduledAt),
                IsComplete: true);

            LogGdprExportCompleted(_logger, appUser.Id, maskedEmail, query.IpAddress, query.UserAgent);

            return Result.Success(response);
        }

        [LoggerMessage(EventId = EventIds.ExportDataCompleted, Level = LogLevel.Information, Message = "GDPR data export completed for user {UserId} ({Email}). IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogGdprExportCompleted(ILogger logger, Guid userId, string email, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.ExportDataRefused, Level = LogLevel.Warning, Message = "GDPR data export refused for user {UserId} ({Email}): the password was missing or wrong. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogGdprExportRefused(ILogger logger, Guid userId, string email, string? ipAddress, string? userAgent);

        [LoggerMessage(EventId = EventIds.ExportDataUnknownUser, Level = LogLevel.Warning, Message = "GDPR data export asked for an account that does not exist: {UserId}. IP: {IpAddress}, UserAgent: {UserAgent}")]
        private static partial void LogGdprExportUnknownUser(ILogger logger, string userId, string? ipAddress, string? userAgent);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("auth/account/data-export", async (
                ExportAccountDataRequest request,
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

                Result<AccountDataExportResponse> queryResult =
                    await handler.Handle(query, cancellationToken);

                if (queryResult.IsFailure)
                {
                    return Results.Problem(queryResult.Error.ToProblemDetails());
                }

                // The answer is a personal-data document. No cache between the API and the caller may
                // keep a copy of it.
                httpContext.Response.Headers.CacheControl = "no-store";

                return Results.Ok(queryResult.Value);
            })
            .RequireAuthorization()
            .RequireRateLimiting("auth-endpoint-limit")
            .WithName(nameof(ExportAccountData))
            .WithTags("Account")
            .Produces<AccountDataExportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
