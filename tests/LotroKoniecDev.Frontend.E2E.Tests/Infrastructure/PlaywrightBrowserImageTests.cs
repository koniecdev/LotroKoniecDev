using System.Diagnostics;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// ADR-0015: the container's server must speak the client's protocol. A pure test: it joins no collection,
/// so it never starts the stack.
/// </summary>
public sealed class PlaywrightBrowserImageTests
{
    [Theory]
    [InlineData("1.61.0", "mcr.microsoft.com/playwright:v1.61.0-noble")]
    [InlineData("1.62.3", "mcr.microsoft.com/playwright:v1.62.3-noble")]
    [InlineData("1.61.0+build.7sha", "mcr.microsoft.com/playwright:v1.61.0-noble")]
    public void BuildTag_ForClientVersion_ReturnsMatchingNobleImageTag(string clientVersion, string expectedTag)
    {
        string tag = PlaywrightBrowserImage.BuildTag(clientVersion);

        tag.ShouldBe(expectedTag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildTag_ForMissingClientVersion_Throws(string? clientVersion)
    {
        Should.Throw<ArgumentException>(() => PlaywrightBrowserImage.BuildTag(clientVersion!));
    }

    [Fact]
    public void Tag_TracksTheClientReleaseVersion_NotThePlaceholderInformationalVersion()
    {
        // Cross-checked against FileVersion: InformationalVersion is a "1.0.0" placeholder, which would
        // give the unpullable v1.0.0-noble.
        Version releaseVersion = new(FileVersionInfo
            .GetVersionInfo(typeof(IPlaywright).Assembly.Location)
            .FileVersion!);

        PlaywrightBrowserImage.Tag.ShouldBe($"mcr.microsoft.com/playwright:v{releaseVersion.ToString(3)}-noble");
    }
}
