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

/// <summary>
/// The account representation: the data the "Moje konto" page renders, carrying the links that say what
/// else this caller may do. Its rel is also the frontend's proof that the token reached this API, so it
/// stays a GET that needs nothing but a login — see <c>Rels.ExportAccountData</c>.
/// It is <b>not</b> the GDPR export, even though the payload is the same. Handing the export over asks
/// for the current password, and that is <see cref="DownloadAccountData"/> (#690, ADR-0052).
/// </summary>
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

            AuthDataExportDto authData = await AccountDataExportMapper.ToDtoAsync(_userManager, appUser);

            AccountDataExportResponse response = new(authData, IsComplete: true);

            // Every visit to the account page passes through here, so this line says "read", not
            // "export". The export has its own two lines, and mixing the two made the audit log unable
            // to answer who took a file (#690).
            LogAccountDataRead(_logger, appUser.Id);

            return Result.Success(response);
        }

        [LoggerMessage(EventId = EventIds.AccountDataRead, Level = LogLevel.Information, Message = "Account data read for user {UserId}")]
        private static partial void LogAccountDataRead(ILogger logger, Guid userId);
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
