using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.Persistence.DomainRepositories;

internal sealed class GameVersionRepository : GenericRepository<GameVersion, GameVersionId>, IGameVersionRepository
{
    public GameVersionRepository(ApplicationWriteDbContext db) : base(db)
    {
    }

    public async Task<bool> ExistsByVersionAsync(LotroNotationVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);

        bool exists = await DbContext.GameVersions
            .AnyAsync(gameVersion => gameVersion.LotroNotationVersion.Value == version.Value, cancellationToken);

        return exists;
    }

    public async Task<IReadOnlyList<GameVersion>> GetUnprocessedDetectedBeforeAsync(
        DateTimeOffset detectedAt,
        CancellationToken cancellationToken)
    {
        List<GameVersion> versions = await DbContext.GameVersions
            .Where(gameVersion =>
                gameVersion.Status == GameVersionStatus.Unprocessed && gameVersion.DetectedAt < detectedAt)
            .ToListAsync(cancellationToken);

        return versions;
    }
}
