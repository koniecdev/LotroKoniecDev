using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// #1157: the boot seed resolves the database context first, so the connection string check stops it
    /// there, before Npgsql reads the string and names a piece of the password in its own error.
    /// </summary>
    [Fact]
    public void AddAuthPersistence_PasswordWithAnUnquotedSemicolon_StopsNamingTheSettingWithoutThePassword()
    {
        using ServiceProvider provider = CreateAuthPersistenceServices(
                "Host=localhost;Database=lotro_auth;Username=postgres;Password=Zq8;Wx3=Kp5")
            .BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        OptionsValidationException exception = Should.Throw<OptionsValidationException>(
            () => scope.ServiceProvider.GetRequiredService<AuthDbContext>());

        string bootError = exception.ToString();
        bootError.ShouldContain("ConnectionStrings:AuthDatabase", Case.Sensitive);
        bootError.ShouldNotContain("Zq8", Case.Insensitive);
        bootError.ShouldNotContain("Wx3", Case.Insensitive);
        bootError.ShouldNotContain("Kp5", Case.Insensitive);
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
