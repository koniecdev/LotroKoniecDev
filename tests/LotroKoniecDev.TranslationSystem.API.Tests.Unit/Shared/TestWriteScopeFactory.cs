using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// Wraps the repository and unit-of-work substitutes in a real <see cref="IServiceScopeFactory"/>. It is
/// the write counterpart of <see cref="TestReadScopeFactory"/>: the translator provisioner does its
/// get-or-create write in a new scope each time, so a caller waiting on the same work never sees the
/// first request's disposed context. This gives it that scope machinery around the substitutes, still
/// with no I/O.
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
