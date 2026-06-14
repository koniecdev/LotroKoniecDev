using FluentValidation;
using LotroKoniecDev.Options;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Persistence.DomainRepositories;
using LotroKoniecDev.TranslationSystem.Persistence.Projections;
using LotroKoniecDev.TranslationSystem.Persistence.Settings;
using LotroKoniecDev.TranslationSystem.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.Persistence;

public static class PersistenceDependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddTranslationPersistence()
        {
            services.AddSingleton<IValidator<ConnectionStringSettings>, ConnectionStringSettingsValidator>();
            services.AddOptionsWithFluentValidation<ConnectionStringSettings>(ConnectionStringSettings.ConfigurationSection);

            services.AddDbContext<ApplicationWriteDbContext>((sp, options) =>
                options.UseNpgsql(
                    sp.GetRequiredService<IOptions<ConnectionStringSettings>>().Value.TranslationDatabase,
                    npgsqlOptions =>
                    {
                        npgsqlOptions.EnableRetryOnFailure(
                            maxRetryCount: 3,
                            maxRetryDelay: TimeSpan.FromSeconds(10),
                            errorCodesToAdd: null);
                        npgsqlOptions.CommandTimeout(30);
                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", DatabaseSchemas.Translation);
                    }));

            services.AddDbContext<ApplicationReadDbContext>((sp, options) =>
                options.UseNpgsql(
                        sp.GetRequiredService<IOptions<ConnectionStringSettings>>().Value.TranslationDatabase,
                        npgsqlOptions =>
                        {
                            npgsqlOptions.EnableRetryOnFailure(
                                maxRetryCount: 3,
                                maxRetryDelay: TimeSpan.FromSeconds(10),
                                errorCodesToAdd: null);
                            npgsqlOptions.CommandTimeout(30);
                        })
                    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

            services.AddScoped<IApplicationReadDbContext>(serviceProvider =>
                serviceProvider.GetRequiredService<ApplicationReadDbContext>());

            services.AddScoped<IUnitOfWork>(serviceProvider =>
                serviceProvider.GetRequiredService<ApplicationWriteDbContext>());

            services.AddScoped<IGameVersionRepository, GameVersionRepository>();
            services.AddScoped<ITranslationRepository, TranslationRepository>();
            services.AddScoped<IPrecomputedTranslationFileStore, PrecomputedTranslationFileStore>();

            return services;
        }
    }
}
