using LotroKoniecDev.TranslationSystem.Persistence.Converters;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;

internal sealed class ApplicationReadDbContext : DbContext, IApplicationReadDbContext
{
    public ApplicationReadDbContext(DbContextOptions<ApplicationReadDbContext> options) : base(options)
    {
    }

    public DbSet<GameVersionReadModel> GameVersions => Set<GameVersionReadModel>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.RegisterAllStronglyTypedIdConverters();
        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DatabaseSchemas.Translation);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IReadModelsEntityFrameworkAssemblyReference).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
