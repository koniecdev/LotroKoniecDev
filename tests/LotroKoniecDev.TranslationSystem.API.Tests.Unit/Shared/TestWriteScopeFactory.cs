using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// The write counterpart of <see cref="TestReadScopeFactory"/>: the translator provisioner writes in a
/// new scope each time.
/// </summary>
internal static class TestWriteScopeFactory
{
    public static IServiceScopeFactory Create(ITranslatorRepository translatorRepository, IUnitOfWork unitOfWork)
    {
        ServiceCollection services = new();
        services.AddScoped<ITranslatorRepository>(_ => translatorRepository);
        services.AddScoped<IUnitOfWork>(_ => unitOfWork);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
