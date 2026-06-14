using System.Security.Cryptography;
using System.Text;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Projects the current Approved set into the precomputed translation file on write (spec 0001:
/// regenerate on version processing, approve, and upsert affecting an Approved row), so the
/// distribution endpoint serves a stored projection without ever building per-request. Single-flight:
/// a process-wide gate serializes concurrent rebuilds, each producing a consistent snapshot of the
/// Approved set. Registered as a singleton, so it resolves the scoped EF services through a fresh scope.
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
            List<ArtifactRow> rows = await readDbContext.Translations
                .Where(translation => translation.Status == TranslationStatus.Approved && translation.RemovedInVersion == null)
                .OrderBy(translation => translation.FileId)
                .ThenBy(translation => translation.GossipId)
                .Select(translation => new ArtifactRow(
                    translation.FileId,
                    translation.GossipId,
                    translation.TranslatedText!,
                    translation.ArgsOrder,
                    translation.ArgsId))
                .ToListAsync(cancellationToken);

            string content = serializer.Serialize(rows);
            string contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            DateTimeOffset now = timeProvider.GetUtcNow();

            Maybe<PrecomputedTranslationFile> existing = await fileStore.GetByLanguageAsync(language, cancellationToken);
            if (existing.HasValue)
            {
                existing.Value.Refresh(content, contentHash, now);
            }
            else
            {
                fileStore.Insert(PrecomputedTranslationFile.Create(language, content, contentHash, now));
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
