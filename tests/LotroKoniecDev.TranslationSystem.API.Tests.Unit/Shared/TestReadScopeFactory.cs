using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// Wraps an <see cref="IApplicationReadDbContext"/> double in a real <see cref="IServiceScopeFactory"/>:
/// the cached-counter handlers resolve their read context from a fresh scope per computation (so a
/// joined caller never observes the initiating request's disposed context), and the unit seam hands
/// them that scope machinery around the in-memory fake — still pure, no I/O.
/// </summary>
internal static class TestReadScopeFactory
{
    public static IServiceScopeFactory Create(IApplicationReadDbContext readDbContext)
    {
        ServiceCollection services = new();
        services.AddScoped<IApplicationReadDbContext>(_ => readDbContext);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
