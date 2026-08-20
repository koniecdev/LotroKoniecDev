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
/// The progress numbers on the public landing page (#309). It groups the active catalog exactly like
/// the translator dashboard's <see cref="Translations.GetTranslationStats"/> does, one count per
/// <see cref="TranslationStatus"/> summed in memory, and adds the newest
/// <see cref="GameVersionStatus.Processed"/> game version.
/// It is a separate, openly anonymous endpoint rather than the dashboard endpoint opened up, so the
/// public contract can stay fixed while the dashboard changes, and nothing but totals is exposed.
///
/// The result is cached in a short-lived <see cref="HybridCache"/> entry (AUDIT-EF-04, #354). This is
/// the most public and least protected endpoint, and recomputing rough counters for every anonymous
/// visitor only costs compute. While the entry is alive, every request shares one computation.
/// The HTTP <c>no-store</c> header stays; only the work on the server is shared.
/// </summary>
internal sealed class GetPublicProgress : IEndpoint
{
    /// <summary>
    /// The one cache entry for the whole process. The endpoint is anonymous, so every visitor sees the
    /// same counters. It is internal so the integration tests can clear it together with their
    /// TRUNCATE.
    /// </summary>
    internal const string CounterCacheKey = "public-progress";

    internal sealed record Query : IQuery<Result<PublicProgressResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<PublicProgressResponse>>
    {
        /// <summary>
        /// Slightly old counters are fine here, the same idea as the delay in ADR-0021. With 30 seconds
        /// the grouped scan runs at most twice a minute, however much traffic the landing page or bots
        /// bring.
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
        /// This runs in its own scope and never in the calling request's. HybridCache runs one factory
        /// for every caller waiting on the same key, and the request that started it can be cancelled,
        /// which disposes its read context, while the others are still waiting. A fresh scope keeps the
        /// shared work alive for them instead of failing on a disposed context.
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

            // "Translated" means the rows that hold Polish. A row only reaches Draft, Approved or
            // NeedsReview once there is Polish, the same grouping the dashboard stats use.
            int translated =
                countByStatus.GetValueOrDefault(TranslationStatus.Draft)
                + approved
                + countByStatus.GetValueOrDefault(TranslationStatus.NeedsReview);

            // The catalog matches the newest processed version. A version that was only detected
            // (Unprocessed) or skipped (Superseded) says nothing about what is distributed.
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
