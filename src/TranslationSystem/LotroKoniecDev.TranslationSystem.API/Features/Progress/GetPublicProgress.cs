using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Progress;

/// <summary>
/// The landing page's public progress snapshot (#309): the same active-catalog bucketing as the
/// translator dashboard's <see cref="Translations.GetTranslationStats"/> (one grouped count per
/// <see cref="TranslationStatus"/>, bucketed in memory) plus the newest
/// <see cref="GameVersionStatus.Processed"/> game version. Deliberately a separate, explicitly
/// anonymous slice rather than an opened-up dashboard endpoint: the public contract stays frozen
/// while the translator dashboard evolves, and nothing beyond aggregate counters is exposed.
/// </summary>
internal sealed class GetPublicProgress : IEndpoint
{
    internal sealed record Query : IQuery<Result<PublicProgressResponse>>;

    internal sealed class Handler : IQueryHandler<Query, Result<PublicProgressResponse>>
    {
        private readonly IApplicationReadDbContext _readDbContext;

        public Handler(IApplicationReadDbContext readDbContext)
        {
            _readDbContext = readDbContext;
        }

        public async ValueTask<Result<PublicProgressResponse>> Handle(Query query, CancellationToken cancellationToken)
        {
            Dictionary<TranslationStatus, int> countByStatus = await _readDbContext.Translations
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
            string? currentGameVersion = await _readDbContext.GameVersions
                .Where(gameVersion => gameVersion.Status == GameVersionStatus.Processed)
                .OrderByDescending(gameVersion => gameVersion.DetectedAt)
                .Select(gameVersion => gameVersion.LotroNotationVersion)
                .FirstOrDefaultAsync(cancellationToken);

            PublicProgressResponse progress = new(
                Total: total,
                Translated: translated,
                Approved: approved,
                CurrentGameVersion: currentGameVersion);

            return Result.Success(progress);
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
