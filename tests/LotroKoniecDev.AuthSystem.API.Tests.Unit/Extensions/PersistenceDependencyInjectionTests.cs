using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Extensions;

public sealed class PersistenceDependencyInjectionTests
{
    /// <summary>#923: the boxes carry <c>Timeout=60</c>, and the database context opens within 20 seconds.</summary>
    [Fact]
    public void AddAuthPersistence_BoxConnectionString_CapsTheOpenTimeoutAtTwentySeconds()
    {
        using ServiceProvider provider = CreateAuthPersistenceServices(
                "Host=localhost;Database=lotro_auth;Username=postgres;Password=changeme;Timeout=60")
            .BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        AuthDbContext context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()).Timeout.ShouldBe(20);
    }

    private static ServiceCollection CreateAuthPersistenceServices(string connectionString)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ConnectionStringSettings.ConfigurationSection}:{nameof(ConnectionStringSettings.AuthDatabase)}"] =
                    connectionString
            })
            .Build());
        services.AddAuthPersistence();

        return services;
    }
}
