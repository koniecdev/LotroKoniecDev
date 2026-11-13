using LotroKoniecDev.TranslationSystem.Persistence.Configurations;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;

internal sealed class TranslationSaveLedger : ITranslationSaveLedger
{
    // ON CONFLICT makes a retried insert harmless: EnableRetryOnFailure may run it again after a
    // commit whose answer was lost.
    private static readonly string InsertSql =
        $"""
         INSERT INTO "{DatabaseSchemas.Translation}"."{TranslationSaveConfiguration.TableName}"
             ("{nameof(TranslationSave.Id)}", "{nameof(TranslationSave.TranslatorId)}", "{nameof(TranslationSave.SavedAt)}", "{nameof(TranslationSave.ReplacedPolish)}")
         VALUES (@id, @translatorId, @savedAt, @replacedPolish)
         ON CONFLICT ("{nameof(TranslationSave.Id)}") DO NOTHING
         """;

    private readonly ApplicationWriteDbContext _dbContext;

    public TranslationSaveLedger(ApplicationWriteDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<TranslationSaveCounts> RecordAsync(
        Guid saveId,
        TranslatorId translatorId,
        bool replacedPolish,
        DateTimeOffset savedAt,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        DateTimeOffset windowStart = savedAt - window;

        await _dbContext.TranslationSaves
            .Where(translationSave => translationSave.SavedAt <= windowStart)
            .ExecuteDeleteAsync(cancellationToken);

        await _dbContext.Database.ExecuteSqlRawAsync(
            InsertSql,
            [
                new NpgsqlParameter("id", saveId),
                new NpgsqlParameter("translatorId", translatorId.Value),
                new NpgsqlParameter("savedAt", savedAt),
                new NpgsqlParameter("replacedPolish", replacedPolish)
            ],
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

    public async Task RemoveAsync(Guid saveId, CancellationToken cancellationToken)
    {
        await _dbContext.TranslationSaves
            .Where(translationSave => translationSave.Id == saveId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
