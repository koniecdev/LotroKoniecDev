using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Settings;

/// <summary>
/// The AuthSystem Data Protection keyring startup guard (M6-04).
/// </summary>
public sealed class DataProtectionGuardTests
{
    private const string Production = "Production";
    private const string Staging = "Staging";
    private const string Development = "Development";
    private const string Testing = "Testing";

    [Theory]
    [InlineData(Development)]
    [InlineData(Testing)]
    public void GuardKeyRingPath_NonDeployedEnvironmentWithEmptyPath_DoesNotThrow(string environmentName)
    {
        DataProtectionSettings settings = new() { KeyRingPath = null };

        Action act = () => DataProtectionExtensions.GuardKeyRingPath(settings, HostEnvironment(environmentName));

        act.ShouldNotThrow();
    }

    [Theory]
    [InlineData(Production, null)]
    [InlineData(Production, "")]
    [InlineData(Production, "   ")]
    [InlineData(Staging, null)]
    [InlineData(Staging, "")]
    [InlineData(Staging, "   ")]
    public void GuardKeyRingPath_DeployedEnvironmentWithMissingPath_ThrowsNamingTheKey(
        string environmentName,
        string? keyRingPath)
    {
        DataProtectionSettings settings = new() { KeyRingPath = keyRingPath };

        Action act = () => DataProtectionExtensions.GuardKeyRingPath(settings, HostEnvironment(environmentName));

        InvalidOperationException exception = act.ShouldThrow<InvalidOperationException>();
        exception.Message.ShouldContain("DataProtection:KeyRingPath");
        exception.Message.ShouldContain(environmentName);
    }

    [Theory]
    [InlineData(Production)]
    [InlineData(Staging)]
    public void GuardKeyRingPath_DeployedEnvironmentWithPath_DoesNotThrow(string environmentName)
    {
        DataProtectionSettings settings = new() { KeyRingPath = "/keys" };

        Action act = () => DataProtectionExtensions.GuardKeyRingPath(settings, HostEnvironment(environmentName));

        act.ShouldNotThrow();
    }

    private static IHostEnvironment HostEnvironment(string environmentName)
    {
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return environment;
    }
}
