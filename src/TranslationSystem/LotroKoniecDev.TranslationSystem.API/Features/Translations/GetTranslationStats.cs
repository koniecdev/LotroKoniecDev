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
/// The mini-dashboard's progress counters (M3-05): total / translated / approved / remaining over the
/// active (non-removed) catalog. Reads the POCO read model — never the write aggregate (CQRS, ADR-0002
/// amendment) — with a single grouped count per <see cref="TranslationStatus"/>, then buckets in
/// memory, so it stays one cheap round-trip regardless of catalog size. Counters only, by design
/// (YAGNI — not analytics).
///
/// Served from a short-TTL <see cref="HybridCache"/> entry (AUDIT-EF-04/#354) under its own key —
/// deliberately not shared with <see cref="Progress.GetPublicProgress"/>: the slices stay
/// independent and their responses differ; the cost is one extra grouped scan per TTL window.
/// </summary>
internal sealed class GetTranslationStats : IEndpoint
{
    /// <summary>
    /// One entry for the whole dashboard — the counters are catalog-wide, not per user. Internal so
    /// the integration-test reset can evict it alongside its TRUNCATE.
    /// </summary>
    internal const string CounterCacheKey = "translation-stats";

    internal sealed record Query : IQuery<Result<TranslationStatsResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<TranslationStatsResponse>>
    {
        /// <summary>
        /// Bounded staleness on counters is fine (same philosophy as the ADR-0021 debounce); 30 s
        /// keeps the dashboard responsive after an approve while deduplicating request bursts.
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
        /// Computes on its OWN scope, never the calling request's: HybridCache runs ONE factory for
        /// all concurrently joined callers, and the initiating request can abort — disposing its
        /// request-scoped read context — while others stay joined. A fresh scope keeps the shared
        /// computation alive for the survivors instead of faulting them with a disposed context.
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

            // "Translated" = the rows that carry Polish content. The domain only reaches Draft,
            // Approved or NeedsReview once Polish exists (Translation.ProvideTranslation / Approve /
            // ApplySourceChange), so summing exactly those three buckets matches the contract by
            // construction — rather than "everything but Untranslated", which would also fold in any
            // future status.
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
