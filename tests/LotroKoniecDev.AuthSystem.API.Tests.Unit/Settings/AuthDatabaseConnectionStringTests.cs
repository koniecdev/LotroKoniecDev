using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Settings;

/// <summary>
/// #923: the sign-in server gives up opening a database connection after 20 seconds, whatever the box's
/// connection string says. The website waits 30 seconds for the sign-in server, so the sign-in server has
/// to give up first.
/// </summary>
public sealed class AuthDatabaseConnectionStringTests
{
    private const string ConnectionStringWithoutTimeout =
        "Host=localhost;Database=lotro_auth;Username=postgres;Password=changeme";

    /// <summary>
    /// The boxes carry <c>Timeout=60</c> today. <c>Timeout=0</c> would make Npgsql wait forever.
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

    [Fact]
    public void WithOpenTimeout_NeonConnectionString_KeepsEveryOtherSetting()
    {
        NpgsqlConnectionStringBuilder result = new(AuthDatabaseConnectionString.WithOpenTimeout(
            "Host=neon.example;Database=lotro_auth;Username=auth_user;Password=changeme;Ssl Mode=Require;Timeout=60"));

        result.Host.ShouldBe("neon.example");
        result.Database.ShouldBe("lotro_auth");
        result.Username.ShouldBe("auth_user");
        result.Password.ShouldBe("changeme");
        result.SslMode.ShouldBe(SslMode.Require);
        result.Timeout.ShouldBe(20);
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
