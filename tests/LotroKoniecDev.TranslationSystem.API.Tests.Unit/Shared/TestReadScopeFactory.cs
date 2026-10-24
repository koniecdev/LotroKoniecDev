using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// The handlers that cache counters resolve their read context from a new scope per computation, so
/// they need a real <see cref="IServiceScopeFactory"/> around the fake.
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
