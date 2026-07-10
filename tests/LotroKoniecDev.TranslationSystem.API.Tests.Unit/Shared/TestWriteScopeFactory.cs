using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// Wraps repository/unit-of-work doubles in a real <see cref="IServiceScopeFactory"/> — the write
/// twin of <see cref="TestReadScopeFactory"/>: the translator provisioner resolves its authoritative
/// get-or-create write from a fresh scope per resolution (so a joined caller never observes the
/// initiating request's disposed context), and the unit seam hands it that scope machinery around
/// the substitutes — still pure, no I/O.
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
