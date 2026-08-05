using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Hateoas.AccountAggregateFactories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed partial class ExportAccountData : IApiEndpoint
{
    internal sealed record Query(string UserId) : IQuery<Result<AccountDataExportResponse>>;

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
                return Result.Failure<AccountDataExportResponse>(AuthErrors.UserNotFound);
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

            LogGdprExportCompleted(_logger, appUser.Id);

            return Result.Success(response);
        }

        [LoggerMessage(EventId = EventIds.ExportDataCompleted, Level = LogLevel.Information, Message = "GDPR data export completed for user {UserId}")]
        private static partial void LogGdprExportCompleted(ILogger logger, Guid userId);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("auth/account/data-export", async (
                ClaimsPrincipal user,
                IQueryHandler<Query, Result<AccountDataExportResponse>> handler,
                IAccountAggregateLinkFactory accountAggregateLinkFactory,
                CancellationToken cancellationToken) =>
            {
                string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? user.FindFirstValue(OpenIddictConstants.Claims.Subject);

                if (string.IsNullOrEmpty(userId))
                {
                    return Results.Unauthorized();
                }

                Query query = new(userId);

                Result<AccountDataExportResponse> queryResult =
                    await handler.Handle(query, cancellationToken);

                if (queryResult.IsFailure)
                {
                    return Results.Problem(queryResult.Error.ToProblemDetails());
                }

                return HateoasResults.Ok(queryResult.Value, async r =>
                {
                    r.Links = await accountAggregateLinkFactory.CreateAccountLinksAsync(
                        isEmailConfirmed: r.AuthData.EmailConfirmed,
                        isDeletionScheduled: r.AuthData.DeletionScheduledAt is not null);
                });
            })
            .RequireAuthorization()
            .RequireRateLimiting("auth-endpoint-limit")
            .WithName(nameof(ExportAccountData))
            .WithTags("Account")
            .Produces<AccountDataExportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
