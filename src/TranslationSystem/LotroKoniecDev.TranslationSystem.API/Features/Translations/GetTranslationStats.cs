using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The mini-dashboard's progress counters (M3-05): total / translated / approved / remaining over the
/// active (non-removed) catalog. Reads the POCO read model — never the write aggregate (CQRS, ADR-0002
/// amendment) — with a single grouped count per <see cref="TranslationStatus"/>, then buckets in
/// memory, so it stays one cheap round-trip regardless of catalog size. Counters only, by design
/// (YAGNI — not analytics).
/// </summary>
internal sealed class GetTranslationStats : IEndpoint
{
    internal sealed record Query : IQuery<Result<TranslationStatsResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<TranslationStatsResponse>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<TranslationStatsResponse>> Handle(Query query, CancellationToken cancellationToken)
        {
            Dictionary<TranslationStatus, int> countByStatus = await _readDbContext.Translations
                .Where(translation => translation.RemovedInVersion == null)
                .GroupBy(translation => translation.Status)
                .Select(group => new { Status = group.Key, Count = group.Count() })
                .ToDictionaryAsync(group => group.Status, group => group.Count, cancellationToken);

            int total = countByStatus.Values.Sum();
            int approved = countByStatus.GetValueOrDefault(TranslationStatus.Approved);

            // "Translated" = the rows that carry Polish content. The domain only reaches Draft,
            // Approved or NeedsReview once Polish exists (Translation.ProvideTranslation / Approve /
            // ApplySourceChange), so summing exactly those three buckets matches the contract by
            // construction — rather than "everything but Untranslated", which would also fold in any
            // future status.
            int translated =
                countByStatus.GetValueOrDefault(TranslationStatus.Draft)
                + approved
                + countByStatus.GetValueOrDefault(TranslationStatus.NeedsReview);

            TranslationStatsResponse stats = new(
                Total: total,
                Translated: translated,
                Approved: approved,
                Remaining: total - approved);

            return Result.Success(stats);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/translations/stats", async (
                IQueryHandler<Query, Result<TranslationStatsResponse>> handler,
                CancellationToken cancellationToken) =>
            {
                Result<TranslationStatsResponse> result = await handler.Handle(new Query(), cancellationToken);

                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(GetTranslationStats))
            .WithTags("Translations")
            .RequireAuthorization(AuthorizationPolicies.RequireTranslatorRole)
            .Produces<TranslationStatsResponse>(StatusCodes.Status200OK);
    }
}
