using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// Returns one translation in full by id (spec 0001), read from the POCO read model — never the
/// write aggregate (CQRS, ADR-0002 amendment). An unknown (or all-zeros) id is a <c>NotFound</c>.
/// </summary>
internal sealed class GetTranslation : IEndpoint
{
    internal sealed record Query(TranslationId Id) : IQuery<Result<TranslationDetailResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<TranslationDetailResponse>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<TranslationDetailResponse>> Handle(Query query, CancellationToken cancellationToken)
        {
            // An all-zeros id never identifies a row — short-circuit before touching the database.
            if (query.Id == TranslationId.Empty)
            {
                return Result.Failure<TranslationDetailResponse>(DomainErrors.TranslationEntity.NotFound(query.Id));
            }

            TranslationDetailResponse? response = await _readDbContext.Translations
                .Where(translation => translation.Id == query.Id)
                .Select(TranslationProjections.ToDetail)
                .FirstOrDefaultAsync(cancellationToken);

            return response is null
                ? Result.Failure<TranslationDetailResponse>(DomainErrors.TranslationEntity.NotFound(query.Id))
                : Result.Success(response);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/translations/{id:guid}", async (
                Guid id,
                IQueryHandler<Query, Result<TranslationDetailResponse>> handler,
                CancellationToken cancellationToken) =>
            {
                Result<TranslationDetailResponse> result = await handler.Handle(new Query(new TranslationId(id)), cancellationToken);

                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(GetTranslation))
            .WithTags("Translations")
            .RequireAuthorization(AuthorizationPolicies.RequireTranslatorRole)
            .Produces<TranslationDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
