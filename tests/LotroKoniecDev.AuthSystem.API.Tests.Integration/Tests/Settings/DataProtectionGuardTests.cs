using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Pure unit coverage for the AuthSystem Data Protection keyring startup guard (M6-04). It lives in
/// the integration project only because the AuthSystem has no API unit project; it instantiates no
/// factory and starts no container.
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

        Action act = () => DataProtectionExtensions.GuardKeyRingPath(settings, new FakeWebHostEnvironment(environmentName));

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

        Action act = () => DataProtectionExtensions.GuardKeyRingPath(settings, new FakeWebHostEnvironment(environmentName));

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

        Action act = () => DataProtectionExtensions.GuardKeyRingPath(settings, new FakeWebHostEnvironment(environmentName));

        act.ShouldNotThrow();
    }
}

file sealed class FakeWebHostEnvironment : IWebHostEnvironment
{
    public FakeWebHostEnvironment(string environmentName)
    {
        EnvironmentName = environmentName;
    }

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "auth-tests";
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
    public string WebRootPath { get; set; } = string.Empty;
    public IFileProvider WebRootFileProvider { get; set; } = null!;
}
