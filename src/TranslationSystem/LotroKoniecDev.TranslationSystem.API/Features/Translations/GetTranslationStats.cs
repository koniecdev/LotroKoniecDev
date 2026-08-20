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
/// The progress counters on the mini-dashboard (M3-05): total, translated, approved and remaining over
/// the catalog rows that are not removed. It reads the read model and never the write aggregate (CQRS,
/// ADR-0002 amendment), with one grouped count per <see cref="TranslationStatus"/> that is then summed
/// in memory, so it stays one cheap query however large the catalog is. Counters only, on purpose. This
/// is not analytics.
///
/// The result is cached in a short-lived <see cref="HybridCache"/> entry (AUDIT-EF-04, #354) under its
/// own key, deliberately not shared with <see cref="Progress.GetPublicProgress"/>. The two endpoints
/// stay independent and their responses differ. The cost is one extra grouped scan per cache window.
/// </summary>
internal sealed class GetTranslationStats : IEndpoint
{
    /// <summary>
    /// One entry for the whole dashboard, because the counters cover the catalog and not a single user.
    /// It is internal so the integration tests can clear it together with their TRUNCATE.
    /// </summary>
    internal const string CounterCacheKey = "translation-stats";

    internal sealed record Query : IQuery<Result<TranslationStatsResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<TranslationStatsResponse>>
    {
        /// <summary>
        /// Slightly old counters are fine here, the same idea as the delay in ADR-0021. With 30 seconds
        /// the dashboard still reacts quickly to an approve while a burst of requests costs one scan.
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
        /// This runs in its own scope and never in the calling request's. HybridCache runs one factory
        /// for every caller waiting on the same key, and the request that started it can be cancelled,
        /// which disposes its read context, while the others are still waiting. A fresh scope keeps the
        /// shared work alive for them instead of failing on a disposed context.
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

            // "Translated" means the rows that hold Polish. A row only reaches Draft, Approved or
            // NeedsReview once there is Polish, through Translation.ProvideTranslation, Approve or
            // ApplySourceChange, so adding exactly those three matches the contract. Counting
            // "everything except Untranslated" would also pick up any status added later.
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
