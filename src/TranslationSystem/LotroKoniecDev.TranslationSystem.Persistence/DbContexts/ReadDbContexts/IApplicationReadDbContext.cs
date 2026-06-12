using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.GameVersionAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;

public interface IApplicationReadDbContext
{
    DbSet<GameVersionReadModel> GameVersions { get; }
}
