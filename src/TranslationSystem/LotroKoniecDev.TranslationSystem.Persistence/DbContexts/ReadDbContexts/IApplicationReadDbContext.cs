using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Projections;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;

public interface IApplicationReadDbContext
{
    DbSet<GameVersionReadModel> GameVersions { get; }

    DbSet<TranslationReadModel> Translations { get; }

    DbSet<PrecomputedTranslationFileReadModel> PrecomputedTranslationFiles { get; }

    DbSet<TranslatorReadModel> Translators { get; }
}
