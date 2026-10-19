using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Pure unit coverage for the connection string the migrator hands to the design-time factory (#1157).
/// It lives in the integration project because the factory is internal to the Persistence assembly,
/// whose internals are visible only to this project. It starts no container and opens no connection.
/// </summary>
public sealed class AuthDbContextDesignTimeFactoryTests
{
    private readonly AuthDbContextDesignTimeFactory _factory = new();

    [Theory]
    [InlineData("Host=db;Database=lotro_auth;Username=deploy;Password=changeme")]
    [InlineData("Host=db;Database=lotro_auth;Username=deploy;Password=\"Zq8;Wx3=Kp5\"")]
    public void CreateDbContext_WithReadableConnection_KeepsTheConnectionStringAsGiven(string connectionString)
    {
        using AuthDbContext context = _factory.CreateDbContext(["--connection", connectionString]);

        context.Database.GetConnectionString().ShouldBe(connectionString);
    }

    /// <summary>
    /// The migrator prints every inner error in full, so the error must name the setting and carry
    /// neither Npgsql's error nor any part of the password.
    /// </summary>
    [Theory]
    [InlineData("Host=db;Database=lotro_auth;Username=deploy;Password=Zq8;Wx3=Kp5", "Zq8", "Wx3", "Kp5")]
    [InlineData("Host=db;Database=lotro_auth;Username=deploy;Password=Zq8;Wx3=", "Zq8", "Wx3")]
    [InlineData("Host=db;Port=Zq8Wx3Kp5;Database=lotro_auth;Username=deploy;Password=x", "Zq8Wx3Kp5")]
    public void CreateDbContext_WithUnreadableConnection_ThrowsNamingTheSettingWithoutThePassword(
        string connectionString,
        params string[] passwordParts)
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => _factory.CreateDbContext(["--connection", connectionString]));

        exception.Message.ShouldContain("--connection", Case.Sensitive);
        exception.Message.ShouldContain("ConnectionStrings:AuthDatabase", Case.Sensitive);
        exception.InnerException.ShouldBeNull();
        foreach (string passwordPart in passwordParts)
        {
            exception.Message.ShouldNotContain(passwordPart, Case.Insensitive);
        }
    }
}
