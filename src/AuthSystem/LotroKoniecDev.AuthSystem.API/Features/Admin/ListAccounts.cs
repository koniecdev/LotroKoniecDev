using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.API.ApiErrors;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Hateoas.AccountAdministrationFactories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Admin;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Features.Admin;

/// <summary>
/// The admin's list of accounts, where the Reviewer role is given and taken away (#1488). Erased accounts
/// are left out: they keep only an anonymous row.
/// </summary>
internal sealed class ListAccounts : IApiEndpoint
{
    public const int PageSize = 50;

    internal sealed record Query(int Page) : IQuery<Result<AccountListResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<AccountListResponse>>
    {
        private readonly AuthDbContext _dbContext;

        public Handler(AuthDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async ValueTask<Result<AccountListResponse>> Handle(Query query, CancellationToken cancellationToken)
        {
            if (query.Page < 1)
            {
                return Result.Failure<AccountListResponse>(AuthErrors.AccountListPageInvalid);
            }

            IQueryable<ApplicationUser> accounts = _dbContext.Users
                .AsNoTracking()
                .Where(user => !user.Email!.EndsWith(AnonymizationConstants.EmailDomain));

            int totalCount = await accounts.CountAsync(cancellationToken);

            var page = await accounts
                .OrderBy(user => user.UserName)
                .ThenBy(user => user.Id)
                .Skip((query.Page - 1) * PageSize)
                .Take(PageSize)
                .Select(user => new { user.Id, user.UserName, user.Email, user.DeletionScheduledAt })
                .ToListAsync(cancellationToken);

            List<Guid> accountIds = page.Select(account => account.Id).ToList();

            // One query for the whole page, not one per row.
            var roleRows = await _dbContext.UserRoles
                .Where(userRole => accountIds.Contains(userRole.UserId))
                .Join(
                    _dbContext.Roles,
                    userRole => userRole.RoleId,
                    role => role.Id,
                    (userRole, role) => new { userRole.UserId, role.Name })
                .ToListAsync(cancellationToken);

            ILookup<Guid, string> rolesByAccount = roleRows
                .Where(row => row.Name is not null)
                .ToLookup(row => row.UserId, row => row.Name!);

            List<AccountSummaryResponse> items = page
                .Select(account => new AccountSummaryResponse(
                    account.Id,
                    account.UserName ?? string.Empty,
                    account.Email ?? string.Empty,
                    rolesByAccount[account.Id].Order(StringComparer.Ordinal).ToList(),
                    account.DeletionScheduledAt))
                .ToList();

            return Result.Success(new AccountListResponse(items, query.Page, PageSize, totalCount));
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("auth/admin/accounts", async (
                IQueryHandler<Query, Result<AccountListResponse>> handler,
                IAccountAdministrationLinkFactory linkFactory,
                CancellationToken cancellationToken,
                [FromQuery] int page = 1) =>
            {
                Result<AccountListResponse> result = await handler.Handle(new Query(page), cancellationToken);

                if (result.IsFailure)
                {
                    return Results.Problem(result.Error.ToProblemDetails());
                }

                return HateoasResults.Ok(result.Value, async response =>
                {
                    foreach (AccountSummaryResponse account in response.Items)
                    {
                        account.Links = await linkFactory.CreateAccountLinksAsync(
                            account.UserId,
                            account.Roles,
                            isDeletionScheduled: account.DeletionScheduledAt is not null);
                    }

                    response.Links = await linkFactory.CreateListLinksAsync(
                        response.Page,
                        hasNextPage: (long)response.Page * response.PageSize < response.TotalCount);
                });
            })
            .RequireAuthorization(AdminTokenPolicy.Name)
            .WithName(nameof(ListAccounts))
            .WithTags("Admin")
            .Produces<AccountListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }
}
