using LotroKoniecDev.AuthSystem.Persistence.Settings;
using Npgsql;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Settings;

public sealed class AuthDatabaseConnectionStringTests
{
    private const string ConnectionStringWithoutTimeout =
        "Host=localhost;Database=lotro_auth;Username=postgres;Password=changeme";

    /// <summary>
    /// #923: at most 20 seconds. The boxes carry <c>Timeout=60</c> today, and Npgsql reads <c>Timeout=0</c>
    /// as no limit at all.
    /// </summary>
    [Theory]
    [InlineData(60, 20)]
    [InlineData(21, 20)]
    [InlineData(0, 20)]
    [InlineData(20, 20)]
    [InlineData(5, 5)]
    [InlineData(1, 1)]
    public void CapOpenTimeout_ConfiguredOpenTimeout_KeepsItAtTwentySecondsOrLess(int configuredSeconds, int expectedSeconds)
    {
        string capped = AuthDatabaseConnectionString.CapOpenTimeout(
            $"{ConnectionStringWithoutTimeout};Timeout={configuredSeconds}");

        new NpgsqlConnectionStringBuilder(capped).Timeout.ShouldBe(expectedSeconds);
    }

    [Fact]
    public void CapOpenTimeout_NoConfiguredOpenTimeout_StaysWithinTwentySeconds()
    {
        string capped = AuthDatabaseConnectionString.CapOpenTimeout(ConnectionStringWithoutTimeout);

        new NpgsqlConnectionStringBuilder(capped).Timeout.ShouldBeInRange(1, 20);
    }

    /// <summary>#923: only the open limit changes, so the box's Neon string keeps working.</summary>
    [Fact]
    public void CapOpenTimeout_NeonConnectionString_KeepsEveryOtherSetting()
    {
        NpgsqlConnectionStringBuilder result = new(AuthDatabaseConnectionString.CapOpenTimeout(
            "Host=neon.example;Database=lotro_auth;Username=auth_user;Password=changeme;Ssl Mode=Require;Timeout=60"));

        result.Host.ShouldBe("neon.example");
        result.Database.ShouldBe("lotro_auth");
        result.Username.ShouldBe("auth_user");
        result.Password.ShouldBe("changeme");
        result.SslMode.ShouldBe(SslMode.Require);
    }
}
