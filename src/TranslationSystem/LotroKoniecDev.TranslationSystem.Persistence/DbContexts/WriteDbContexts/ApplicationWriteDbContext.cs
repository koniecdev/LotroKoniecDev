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

            // Accept only after the commit: a fault at commit re-runs this lambda, and changes accepted
            // now would leave the retry with nothing pending, so they would be lost.
            await SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            ChangeTracker.AcceptAllChanges();
        });
    }

    /// <inheritdoc />
    public async Task SaveChangesAndClearAsync(CancellationToken cancellationToken = default)
    {
        await SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
        ChangeTracker.Clear();
    }

    /// <inheritdoc />
    public void ClearChangeTracker() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DatabaseSchemas.Translation);

        // For the trigram indexes in TranslationConfiguration. The migrator role must be allowed to
        // run CREATE EXTENSION.
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        base.OnModelCreating(modelBuilder);
    }
}
