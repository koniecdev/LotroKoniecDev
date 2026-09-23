using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Extensions;

/// <summary>
/// The two configuration rules of the admin seed (ADR-0056). The seed itself runs against a real
/// database in <c>AdminSeedingTests</c>.
/// </summary>
public sealed class DatabaseSeederExtensionsTests
{
    private const string ConfiguredPassword = "ConfiguredAdmin1!";

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void ReadBootstrapPassword_InDevelopmentOrTesting_ReturnsTheConfiguredPassword(string environmentName)
    {
        string? password = DatabaseSeederExtensions.ReadBootstrapPassword(
            Configuration(password: ConfiguredPassword),
            HostEnvironment(environmentName),
            NullLogger.Instance);

        password.ShouldBe(ConfiguredPassword);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ReadBootstrapPassword_OutsideDevelopmentAndTesting_ReturnsNull(string environmentName)
    {
        string? password = DatabaseSeederExtensions.ReadBootstrapPassword(
            Configuration(password: ConfiguredPassword),
            HostEnvironment(environmentName),
            NullLogger.Instance);

        password.ShouldBeNull();
    }

    /// <summary>
    /// The warning tells the operator to remove a password that no longer does anything, so it is the
    /// behaviour here, and it must never print the value itself.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ReadBootstrapPassword_OutsideDevelopmentAndTesting_WarnsWithoutPrintingThePassword(
        string environmentName)
    {
        CapturingLogger<DatabaseSeederExtensionsTests> logger = new();

        DatabaseSeederExtensions.ReadBootstrapPassword(
            Configuration(password: ConfiguredPassword),
            HostEnvironment(environmentName),
            logger);

        CapturingLogger<DatabaseSeederExtensionsTests>.LogEntry entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.EventId.ShouldBe(2352);
        entry.Message.ShouldNotContain(ConfiguredPassword);
    }

    [Theory]
    [InlineData("Development", null)]
    [InlineData("Development", "")]
    [InlineData("Development", "   ")]
    [InlineData("Testing", "")]
    [InlineData("Production", null)]
    [InlineData("Production", "   ")]
    public void ReadBootstrapPassword_BlankPassword_ReturnsNullWithoutWarning(
        string environmentName,
        string? configuredPassword)
    {
        CapturingLogger<DatabaseSeederExtensionsTests> logger = new();

        string? password = DatabaseSeederExtensions.ReadBootstrapPassword(
            Configuration(password: configuredPassword),
            HostEnvironment(environmentName),
            logger);

        password.ShouldBeNull();
        logger.Entries.ShouldBeEmpty();
    }

    /// <summary>
    /// Compose turns an unset AUTH_ADMIN_USERNAME into an empty string. Passing that on to Identity
    /// crashed every startup of a box that set only the admin e-mail.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadAdminUsername_BlankUsername_FallsBackToTheDefault(string? configuredUsername)
    {
        string username = DatabaseSeederExtensions.ReadAdminUsername(Configuration(username: configuredUsername));

        username.ShouldBe(DatabaseSeederExtensions.DefaultAdminUsername);
    }

    [Fact]
    public void ReadAdminUsername_ConfiguredUsername_ReturnsIt()
    {
        string username = DatabaseSeederExtensions.ReadAdminUsername(Configuration(username: "lotroadmin"));

        username.ShouldBe("lotroadmin");
    }

    private static IConfiguration Configuration(string? username = null, string? password = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminUser:Username"] = username,
                ["AdminUser:Password"] = password
            })
            .Build();

    private static IWebHostEnvironment HostEnvironment(string environmentName)
    {
        IWebHostEnvironment environment = Substitute.For<IWebHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return environment;
    }
}
