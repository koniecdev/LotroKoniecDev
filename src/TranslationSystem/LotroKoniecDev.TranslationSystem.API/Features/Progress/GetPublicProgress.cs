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
/// The public landing-page numbers (#309): the grouping of <see cref="Translations.GetTranslationStats"/>
/// plus the newest processed game version. A separate anonymous endpoint, not the dashboard one opened
/// up, so the public contract stays fixed and only totals are exposed. Cached because it is the least
/// protected endpoint (AUDIT-EF-04, #354); the HTTP <c>no-store</c> header still applies.
/// </summary>
internal sealed class GetPublicProgress : IEndpoint
{
    /// <summary>
    /// One entry for every visitor. Internal so the integration tests can clear it with their TRUNCATE.
    /// </summary>
    internal const string CounterCacheKey = "public-progress";

    internal sealed record Query : IQuery<Result<PublicProgressResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<PublicProgressResponse>>
    {
        /// <summary>
        /// Slightly old counters are fine (as in ADR-0021): the scan runs at most twice a minute,
        /// whatever traffic the landing page or bots bring.
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
        /// Its own scope: HybridCache shares one factory run with every caller of the key, and the
        /// request that started it may be cancelled and dispose its read context while others wait.
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

            // The same three statuses as the dashboard stats.
            int translated =
                countByStatus.GetValueOrDefault(TranslationStatus.Draft)
                + approved
                + countByStatus.GetValueOrDefault(TranslationStatus.NeedsReview);

            // An unprocessed or superseded version says nothing about what is distributed.
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
