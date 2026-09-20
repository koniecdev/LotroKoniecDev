using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Hateoas.AccountAggregateFactories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// The caller's own account resource. The account pages read it on every view, so it asks for a login
/// and nothing more. The GDPR export used to play this role too; it now asks for the password, so the
/// two had to split (#690).
/// </summary>
internal sealed class GetAccount : IApiEndpoint
{
    internal sealed record Query(string UserId) : IQuery<Result<AccountResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<AccountResponse>>
    {
        private readonly UserManager<ApplicationUser> _userManager;

        public Handler(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        public async ValueTask<Result<AccountResponse>> Handle(Query query, CancellationToken cancellationToken)
        {
            ApplicationUser? appUser = await _userManager.FindByIdAsync(query.UserId);
            if (appUser is null)
            {
                return Result.Failure<AccountResponse>(AuthErrors.UserNotFound);
            }

            IList<string> roles = await _userManager.GetRolesAsync(appUser);

            AccountResponse response = new(new AccountDto(
                appUser.UserName ?? string.Empty,
                appUser.Email ?? string.Empty,
                appUser.EmailConfirmed,
                roles.ToList(),
                appUser.DataProcessingConsentGiven,
                appUser.DataProcessingConsentDate,
                appUser.PrivacyPolicyAccepted,
                appUser.PrivacyPolicyAcceptedDate,
                appUser.TermsOfServiceAccepted,
                appUser.TermsOfServiceAcceptedDate,
                appUser.DeletionScheduledAt));

            return Result.Success(response);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("auth/account", async (
                ClaimsPrincipal user,
                IQueryHandler<Query, Result<AccountResponse>> handler,
                IAccountAggregateLinkFactory accountAggregateLinkFactory,
                CancellationToken cancellationToken) =>
            {
                string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? user.FindFirstValue(OpenIddictConstants.Claims.Subject);

                if (string.IsNullOrEmpty(userId))
                {
                    return Results.Unauthorized();
                }

                Result<AccountResponse> queryResult = await handler.Handle(new Query(userId), cancellationToken);

                if (queryResult.IsFailure)
                {
                    return Results.Problem(queryResult.Error.ToProblemDetails());
                }

                return HateoasResults.Ok(queryResult.Value, async r =>
                {
                    r.Links = await accountAggregateLinkFactory.CreateAccountLinksAsync(
                        isEmailConfirmed: r.Account.EmailConfirmed,
                        isDeletionScheduled: r.Account.DeletionScheduledAt is not null);
                });
            })
            .RequireAuthorization()
            .RequireRateLimiting("auth-endpoint-limit")
            .WithName(nameof(GetAccount))
            .WithTags("Account")
            .Produces<AccountResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
