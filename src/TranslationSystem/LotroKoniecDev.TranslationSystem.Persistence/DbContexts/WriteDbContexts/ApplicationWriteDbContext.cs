using System.Data;
using System.Reflection;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Persistence.Converters;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;

internal sealed class ApplicationWriteDbContext : DbContext, IUnitOfWork
{
    public ApplicationWriteDbContext(DbContextOptions<ApplicationWriteDbContext> options) : base(options)
    {
    }

    public DbSet<GameVersion> GameVersions => Set<GameVersion>();

    public DbSet<Translation> Translations => Set<Translation>();

    public DbSet<PrecomputedTranslationFile> PrecomputedTranslationFiles => Set<PrecomputedTranslationFile>();

    public DbSet<Translator> Translators => Set<Translator>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.RegisterAllStronglyTypedIdConverters();
        base.ConfigureConventions(configurationBuilder);
    }

    /// <inheritdoc />
    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

    /// <inheritdoc />
    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        IExecutionStrategy strategy = Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            await operation(cancellationToken);

            // Save the tracked changes but DEFER accepting them: with retry-on-failure enabled, a
            // transient fault at commit makes the strategy re-run this whole lambda. Were the tracker
            // accepted now (the SaveChanges default), the retry would find nothing pending and silently
            // drop the tracked mutations; deferring the accept until the commit succeeds keeps them
            // re-emittable on retry. The COPY inside `operation` re-runs fresh too, since the failed
            // attempt rolled back.
            await SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            ChangeTracker.AcceptAllChanges();
        });
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DatabaseSchemas.Translation);

        // The trigram GIN indexes on Translations (TranslationConfiguration) need pg_trgm; the
        // migration emits CREATE EXTENSION IF NOT EXISTS, so the migrator role must be allowed to.
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        base.OnModelCreating(modelBuilder);
    }
}
