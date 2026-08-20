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
/// One approved row as the projection reads it: the Polish that ships, plus the English it was
/// approved against, which is only there to be hashed into the row's <c>source_digest</c>
/// (ADR-0047).
/// </summary>
internal sealed record ArtifactSourceRow(
    int FileId,
    long GossipId,
    string TranslatedText,
    string SourceText,
    string? ArgsOrder,
    string? ArgsId);

/// <summary>
/// Writes the rows that are approved right now into the ready-made translation file. Spec 0001 asks
/// for a rebuild after version processing, after an approve, and after an upsert that touches an
/// approved row, so the download endpoint always serves a stored file and never builds one per
/// request. The background worker calls it (PERF-04, ADR-0021).
/// Only one rebuild runs at a time: a gate for the whole process lets them through one by one, and
/// each one sees a consistent set of approved rows. That gate, like the worker's queue, assumes a
/// single API instance.
/// It is a singleton, so it opens its own scope to resolve the scoped EF services.
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

            // The distributed file carries the Polish text and the source's argument columns. Only
            // approved rows that are not removed are included; NeedsReview is the retranslation
            // backlog.
            // The RemovedInVersion check is not redundant with the status: an import can soft-remove an
            // approved row, and the row keeps its Approved status.
            // The rows are streamed and not collected first. The English source is needed only long
            // enough to hash it into the row's source_digest (ADR-0047), so it never sits in memory
            // next to the Polish it belongs to.
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
            // The hex SHA-256 of the UTF-8 body is a contract between the two contexts: it goes out as
            // the ETag, and the patcher refuses a download whose hash differs (AUDIT-SEC-01, #391).
            string contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            DateTimeOffset now = timeProvider.GetUtcNow();

            // One UPDATE refreshes the existing row (PERF-04) without ever loading the previous
            // multi-MB content. Only the first build for a language inserts a row.
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
