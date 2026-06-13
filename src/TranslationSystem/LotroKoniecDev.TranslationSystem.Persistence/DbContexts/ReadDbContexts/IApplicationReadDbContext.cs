using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;

public interface IApplicationReadDbContext
{
    DbSet<GameVersionReadModel> GameVersions { get; }

    DbSet<TranslationReadModel> Translations { get; }
}
