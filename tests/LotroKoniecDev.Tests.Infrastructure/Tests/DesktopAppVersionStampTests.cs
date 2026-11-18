using System.Reflection;
using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.DesktopApp;
using LotroKoniecDev.DesktopApp.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The version the exe carries decides whether it ever updates (spec 0016). A missing stamp falls back
/// to 0.0.0, which never checks, so a renamed key on either side would be silent without these tests.
/// The release smoke checks the stamped value itself on the installed exe.
/// </summary>
public sealed class DesktopAppVersionStampTests
{
    [Fact]
    public void Assembly_AsBuilt_CarriesAStampTheTagParserReads()
    {
        // Act
        string? stamp = typeof(DesktopAppDependencyInjection).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == DesktopAppDependencyInjection.VersionMetadataKey)?.Value;

        // Assert: a normal build is a developer build.
        stamp.ShouldBe("0.0.0");
        DesktopVersion.TryParse(stamp, out DesktopVersion? version).ShouldBeTrue();
        version.IsDevelopmentBuild.ShouldBeTrue();
    }

    [Fact]
    public void BuildServiceProvider_AnyBuild_RegistersTheStampedVersion()
    {
        // Arrange
        using ServiceProvider serviceProvider = DesktopAppDependencyInjection.BuildServiceProvider();

        // Act
        InstalledAppVersion installed = serviceProvider.GetRequiredService<InstalledAppVersion>();

        // Assert
        installed.Value.ShouldBe("0.0.0");
    }
}
