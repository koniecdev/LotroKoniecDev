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
    private const string ConnectionStringWithoutTimeout =
        "Host=localhost;Database=lotro_auth;Username=postgres;Password=changeme";

    /// <summary>
    /// #923: 20 seconds, below the website's 30, whatever the box says. The boxes carry <c>Timeout=60</c>
    /// today, and <c>Timeout=0</c> would make Npgsql wait forever.
    /// </summary>
    [Theory]
    [InlineData(";Timeout=60")]
    [InlineData(";Timeout=0")]
    [InlineData("")]
    public void AddAuthPersistence_AnyConfiguredOpenTimeout_OpensTheConnectionWithinTwentySeconds(string configuredTimeout)
    {
        using ServiceProvider provider = CreateAuthPersistenceServices(ConnectionStringWithoutTimeout + configuredTimeout)
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
