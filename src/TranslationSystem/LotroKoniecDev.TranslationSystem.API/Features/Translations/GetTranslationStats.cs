using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The mini-dashboard counters (M3-05): one grouped count per status, summed in memory, so it stays one
/// cheap query at any catalog size. Its cache key is not shared with
/// <see cref="Progress.GetPublicProgress"/>, so the two endpoints stay independent (AUDIT-EF-04, #354).
/// </summary>
internal sealed class GetTranslationStats : IEndpoint
{
    /// <summary>
    /// One entry for everyone: the counters cover the catalog, not a user. Internal so the integration
    /// tests can clear it with their TRUNCATE.
    /// </summary>
    internal const string CounterCacheKey = "translation-stats";

    internal sealed record Query : IQuery<Result<TranslationStatsResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<TranslationStatsResponse>>
    {
        /// <summary>
        /// Slightly old counters are fine (as in ADR-0021): the dashboard still reacts quickly to an
        /// approve, and a burst of requests costs one scan.
        /// </summary>
        private static readonly HybridCacheEntryOptions CounterTtlEntryOptions = new()
        {
            Expiration = TimeSpan.FromSeconds(30),
            LocalCacheExpiration = TimeSpan.FromSeconds(30)
        };

        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly HybridCache _hybridCache;

        public Handler(IServiceScopeFactory serviceScopeFactory, HybridCache hybridCache)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _hybridCache = hybridCache;
        }

        public async ValueTask<Result<TranslationStatsResponse>> Handle(Query query, CancellationToken cancellationToken)
        {
            TranslationStatsResponse stats = await _hybridCache.GetOrCreateAsync(
                CounterCacheKey,
                this,
                static (self, ct) => self.ComputeStatsAsync(ct),
                CounterTtlEntryOptions,
                cancellationToken: cancellationToken);

            return Result.Success(stats);
        }

        /// <summary>
        /// Its own scope: HybridCache shares one factory run with every caller of the key, and the
        /// request that started it may be cancelled and dispose its read context while others wait.
        /// </summary>
        private async ValueTask<TranslationStatsResponse> ComputeStatsAsync(CancellationToken cancellationToken)
        {
            await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
            IApplicationReadDbContext readDbContext =
                scope.ServiceProvider.GetRequiredService<IApplicationReadDbContext>();

            Dictionary<TranslationStatus, int> countByStatus = await readDbContext.Translations
                .Where(translation => translation.RemovedInVersion == null)
                .GroupBy(translation => translation.Status)
                .Select(group => new { Status = group.Key, Count = group.Count() })
                .ToDictionaryAsync(group => group.Status, group => group.Count, cancellationToken);

            int total = countByStatus.Values.Sum();
            int approved = countByStatus.GetValueOrDefault(TranslationStatus.Approved);

            // Only these three statuses hold Polish. Listing them, not "all but Untranslated", keeps a
            // status added later from being counted by accident.
            int translated =
                countByStatus.GetValueOrDefault(TranslationStatus.Draft)
                + approved
                + countByStatus.GetValueOrDefault(TranslationStatus.NeedsReview);

            return new TranslationStatsResponse(
                Total: total,
                Translated: translated,
                Approved: approved,
                Remaining: total - approved);
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
