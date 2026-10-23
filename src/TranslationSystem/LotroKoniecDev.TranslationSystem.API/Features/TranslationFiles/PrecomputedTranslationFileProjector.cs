using System.Security.Cryptography;
using System.Text;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// <see cref="SourceText"/> is there only to be hashed into the row's <c>source_digest</c> (ADR-0047).
/// </summary>
internal sealed record ArtifactSourceRow(
    int FileId,
    long GossipId,
    string TranslatedText,
    string SourceText,
    string? ArgsOrder,
    string? ArgsId);

/// <summary>
/// Builds the translation file from the rows approved right now (spec 0001). One rebuild runs at a time,
/// so each sees a consistent set of approved rows; that gate, like the worker's queue, assumes a single
/// API instance (ADR-0021).
/// </summary>
internal sealed class PrecomputedTranslationFileProjector : IPrecomputedTranslationFileProjector
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PrecomputedTranslationFileProjector(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task RebuildAsync(string language, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IApplicationReadDbContext readDbContext = scope.ServiceProvider.GetRequiredService<IApplicationReadDbContext>();
            IPrecomputedTranslationFileStore fileStore = scope.ServiceProvider.GetRequiredService<IPrecomputedTranslationFileStore>();
            IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            ITranslationFileSerializer serializer = scope.ServiceProvider.GetRequiredService<ITranslationFileSerializer>();
            TimeProvider timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

            // The RemovedInVersion check is needed: a soft-removed row keeps its Approved status. The
            // source rows are streamed, so each English text is dropped once hashed (ADR-0047).
            List<ArtifactRow> rows = [];

            IAsyncEnumerable<ArtifactSourceRow> sourceRows = readDbContext.Translations
                .Where(translation => translation.Status == TranslationStatus.Approved && translation.RemovedInVersion == null)
                .OrderBy(translation => translation.FileId)
                .ThenBy(translation => translation.GossipId)
                .Select(translation => new ArtifactSourceRow(
                    translation.FileId,
                    translation.GossipId,
                    translation.TranslatedText!,
                    translation.SourceText,
                    translation.ArgsOrder,
                    translation.ArgsId))
                .AsAsyncEnumerable();

            await foreach (ArtifactSourceRow sourceRow in sourceRows.WithCancellation(cancellationToken))
            {
                rows.Add(new ArtifactRow(
                    sourceRow.FileId,
                    sourceRow.GossipId,
                    sourceRow.TranslatedText,
                    sourceRow.ArgsOrder,
                    sourceRow.ArgsId,
                    SourceHash.Compute(sourceRow.SourceText, sourceRow.ArgsOrder, sourceRow.ArgsId).ToWireDigest()));
            }

            string content = serializer.Serialize(rows);
            // A contract with the patcher: it goes out as the ETag, and the patcher refuses a download
            // whose hash differs (AUDIT-SEC-01).
            string contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            DateTimeOffset now = timeProvider.GetUtcNow();

            bool refreshed = await fileStore.TryRefreshAsync(language, content, contentHash, now, cancellationToken);
            if (!refreshed)
            {
                fileStore.Insert(PrecomputedTranslationFile.Create(language, content, contentHash, now));
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
