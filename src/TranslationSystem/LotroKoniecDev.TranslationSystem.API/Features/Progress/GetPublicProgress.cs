using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace LotroKoniecDev.TranslationSystem.API.Features.Progress;

/// <summary>
/// The landing page's public progress snapshot (#309): the same active-catalog bucketing as the
/// translator dashboard's <see cref="Translations.GetTranslationStats"/> (one grouped count per
/// <see cref="TranslationStatus"/>, bucketed in memory) plus the newest
/// <see cref="GameVersionStatus.Processed"/> game version. Deliberately a separate, explicitly
/// anonymous slice rather than an opened-up dashboard endpoint: the public contract stays frozen
/// while the translator dashboard evolves, and nothing beyond aggregate counters is exposed.
///
/// The snapshot is served from a short-TTL <see cref="HybridCache"/> entry (AUDIT-EF-04/#354):
/// this is the most public, least protected endpoint, and recomputing approximate counters per
/// anonymous hit only burns compute — within the TTL every request shares one computation.
/// HTTP-level <c>no-store</c> stays; only the server-side recomputation is deduplicated.
/// </summary>
internal sealed class GetPublicProgress : IEndpoint
{
    /// <summary>
    /// The single process-wide snapshot entry — the endpoint is anonymous, so every visitor shares
    /// the same counters. Internal so the integration-test reset can evict it alongside its TRUNCATE.
    /// </summary>
    internal const string CounterCacheKey = "public-progress";

    internal sealed record Query : IQuery<Result<PublicProgressResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<PublicProgressResponse>>
    {
        /// <summary>
        /// Bounded staleness on counters is fine (same philosophy as the ADR-0021 debounce); 30 s
        /// caps the grouped scan at two per minute regardless of landing-page or bot traffic.
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

        public async ValueTask<Result<PublicProgressResponse>> Handle(Query query, CancellationToken cancellationToken)
        {
            PublicProgressResponse progress = await _hybridCache.GetOrCreateAsync(
                CounterCacheKey,
                this,
                static (self, ct) => self.ComputeSnapshotAsync(ct),
                CounterTtlEntryOptions,
                cancellationToken: cancellationToken);

            return Result.Success(progress);
        }

        /// <summary>
        /// Computes on its OWN scope, never the calling request's: HybridCache runs ONE factory for
        /// all concurrently joined callers, and the initiating request can abort — disposing its
        /// request-scoped read context — while others stay joined. A fresh scope keeps the shared
        /// computation alive for the survivors instead of faulting them with a disposed context.
        /// </summary>
        private async ValueTask<PublicProgressResponse> ComputeSnapshotAsync(CancellationToken cancellationToken)
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

            // "Translated" = the rows that carry Polish content — the domain only reaches Draft,
            // Approved or NeedsReview once Polish exists (same bucketing as the dashboard stats).
            int translated =
                countByStatus.GetValueOrDefault(TranslationStatus.Draft)
                + approved
                + countByStatus.GetValueOrDefault(TranslationStatus.NeedsReview);

            // The catalog is current for the newest PROCESSED version: merely-detected (Unprocessed)
            // and skipped-over (Superseded) versions say nothing about the distributed content.
            string? currentGameVersion = await readDbContext.GameVersions
                .Where(gameVersion => gameVersion.Status == GameVersionStatus.Processed)
                .OrderByDescending(gameVersion => gameVersion.DetectedAt)
                .Select(gameVersion => gameVersion.LotroNotationVersion)
                .FirstOrDefaultAsync(cancellationToken);

            return new PublicProgressResponse(
                Total: total,
                Translated: translated,
                Approved: approved,
                CurrentGameVersion: currentGameVersion);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/progress", async (
                IQueryHandler<Query, Result<PublicProgressResponse>> handler,
                CancellationToken cancellationToken) =>
            {
                Result<PublicProgressResponse> result = await handler.Handle(new Query(), cancellationToken);

                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(GetPublicProgress))
            .WithTags("Progress")
            .AllowAnonymous()
            .Produces<PublicProgressResponse>(StatusCodes.Status200OK);
    }
}
