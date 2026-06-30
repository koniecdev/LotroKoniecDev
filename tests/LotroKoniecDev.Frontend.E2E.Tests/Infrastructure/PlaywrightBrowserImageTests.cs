using System.Diagnostics;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// Locks the browser-image tag derivation so a <c>Microsoft.Playwright</c> bump can never silently
/// drift the in-container server's protocol away from the client (the failure mode behind ADR-0015).
/// Pure and Docker-free — it joins no collection, so it never boots the stack fixture.
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
        // Cross-check against an INDEPENDENT source (FileVersion): Microsoft.Playwright's
        // InformationalVersion is a "1.0.0" placeholder, so a derivation keyed off it would yield the
        // unpullable v1.0.0-noble. FileVersion carries the real release, matching the published tags.
        Version releaseVersion = new(FileVersionInfo
            .GetVersionInfo(typeof(IPlaywright).Assembly.Location)
            .FileVersion!);

        PlaywrightBrowserImage.Tag.ShouldBe($"mcr.microsoft.com/playwright:v{releaseVersion.ToString(3)}-noble");
    }
}
