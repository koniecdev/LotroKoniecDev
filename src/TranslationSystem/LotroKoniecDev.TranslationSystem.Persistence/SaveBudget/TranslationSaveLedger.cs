using LotroKoniecDev.TranslationSystem.Persistence.Configurations;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;

internal sealed class TranslationSaveLedger : ITranslationSaveLedger
{
    // Raw SQL skips the execution strategy, so RecordAsync runs it through one, like the purge and the
    // count. ON CONFLICT then makes a retry after a lost commit answer harmless. Two array parameters,
    // whatever the number of permits, so a fill of thousands of rows stays far below PostgreSQL's limit
    // of 65,535 parameters.
    private static readonly string InsertSql =
        $"""
         INSERT INTO "{DatabaseSchemas.Translation}"."{TranslationSaveConfiguration.TableName}"
             ("{nameof(TranslationSave.Id)}", "{nameof(TranslationSave.TranslatorId)}", "{nameof(TranslationSave.SavedAt)}", "{nameof(TranslationSave.ReplacedPolish)}")
         SELECT permit.id, @translatorId, @savedAt, permit.replaced_polish
         FROM unnest(@ids, @replacedPolish) AS permit(id, replaced_polish)
         ON CONFLICT ("{nameof(TranslationSave.Id)}") DO NOTHING
         """;

    private readonly ApplicationWriteDbContext _dbContext;

    public async Task<TranslationSaveCounts> RecordAsync(
        IReadOnlyList<TranslationSavePermit> permits,
        TranslatorId translatorId,
        DateTimeOffset savedAt,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permits);
        ArgumentOutOfRangeException.ThrowIfZero(permits.Count);

        DateTimeOffset windowStart = savedAt - window;
        Guid[] ids = permits.Select(permit => permit.Id).ToArray();
        bool[] replacedPolish = permits.Select(permit => permit.ReplacedPolish).ToArray();

        await DeleteExpiredAsync(savedAt, window, cancellationToken);

        // A parameter object belongs to one command, so each attempt builds its own.
        await _dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
            async retryCancellationToken => await _dbContext.Database.ExecuteSqlRawAsync(
                InsertSql,
                [
                    new NpgsqlParameter("ids", ids),
                    new NpgsqlParameter("replacedPolish", replacedPolish),
                    new NpgsqlParameter("translatorId", translatorId.Value),
                    new NpgsqlParameter("savedAt", savedAt)
                ],
                retryCancellationToken),
            cancellationToken);

        TranslationSaveCounts? counts = await _dbContext.TranslationSaves
            .Where(translationSave => translationSave.TranslatorId == translatorId && translationSave.SavedAt > windowStart)
            .GroupBy(translationSave => translationSave.TranslatorId)
            .Select(group => new TranslationSaveCounts(
                group.Count(),
                group.Count(translationSave => translationSave.ReplacedPolish)))
            .SingleOrDefaultAsync(cancellationToken);

        return counts ?? new TranslationSaveCounts(0, 0);
    }

    public async Task DeleteExpiredAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken)
    {
        DateTimeOffset windowStart = now - window;

        await _dbContext.TranslationSaves
            .Where(translationSave => translationSave.SavedAt <= windowStart)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task RemoveAsync(IReadOnlyList<Guid> saveIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(saveIds);

        if (saveIds.Count == 0)
        {
            return;
        }

        // A delete by id is safe to run again, so a short fault does not leave a fill's permits spent.
        await _dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
            async retryCancellationToken => await _dbContext.TranslationSaves
                .Where(translationSave => saveIds.Contains(translationSave.Id))
                .ExecuteDeleteAsync(retryCancellationToken),
            cancellationToken);
    }

    public TranslationSaveLedger(ApplicationWriteDbContext dbContext)
    {
        _dbContext = dbContext;
    }
}
