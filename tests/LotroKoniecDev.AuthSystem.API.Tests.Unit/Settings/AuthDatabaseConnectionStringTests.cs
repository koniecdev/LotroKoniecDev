using LotroKoniecDev.AuthSystem.Persistence.Settings;
using Npgsql;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Settings;

public sealed class AuthDatabaseConnectionStringTests
{
    /// <summary>#923: only the open limit changes, so the box's Neon string keeps working.</summary>
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
    }
}
