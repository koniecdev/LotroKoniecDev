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
/// One Approved row as read for projection: the Polish that ships plus the English it was approved
/// against, which exists only to be hashed into the row's <c>source_digest</c> (ADR-0047).
/// </summary>
internal sealed record ArtifactSourceRow(
    int FileId,
    long GossipId,
    string TranslatedText,
    string SourceText,
    string? ArgsOrder,
    string? ArgsId);

/// <summary>
/// Projects the current Approved set into the precomputed translation file (spec 0001: regenerate
/// after version processing, approve, and upsert affecting an Approved row), so the distribution
/// endpoint serves a stored projection without ever building per-request. Invoked by the debounced
/// background worker (PERF-04, ADR-0021).
/// Single-flight: a process-wide gate serializes concurrent rebuilds, each producing a consistent
/// snapshot of the Approved set — the gate (like the worker's queue) assumes a single API replica.
/// Registered as a singleton, so it resolves the scoped EF services through a fresh scope.
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

            // The distributed file carries the Polish text and the source's argument columns; only
            // Approved, non-removed rows are included (NeedsReview is the re-translation backlog).
            // The RemovedInVersion guard is load-bearing, not redundant with the status: an Approved
            // row can later be soft-removed by an import without losing its Approved status.
            // Streamed rather than materialized: the English source is needed only long enough to
            // hash it into the row's source_digest (ADR-0047), so it never joins the artifact in
            // memory alongside the Polish it is hashed for.
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
            // Hex SHA-256 of the UTF-8 body is a cross-context contract: it ships as the distribution
            // ETag and the patcher rejects a download that does not hash-match it (AUDIT-SEC-01/#391).
            string contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            DateTimeOffset now = timeProvider.GetUtcNow();

            // Set-based upsert (PERF-04): a single UPDATE refreshes the existing row without ever
            // loading the previous multi-MB content; only the first build per language inserts.
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
