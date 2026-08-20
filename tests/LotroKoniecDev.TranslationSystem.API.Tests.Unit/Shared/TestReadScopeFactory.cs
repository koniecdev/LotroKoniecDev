using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// Wraps an <see cref="IApplicationReadDbContext"/> substitute in a real
/// <see cref="IServiceScopeFactory"/>. The handlers that cache counters take their read context from a
/// new scope for each computation, so a caller waiting on the same work never sees the first request's
/// disposed context. This gives them that scope machinery around the in-memory fake, still with no
/// I/O.
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
