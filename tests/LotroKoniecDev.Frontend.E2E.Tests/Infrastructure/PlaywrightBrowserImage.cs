using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// Works out the Playwright browser-server image tag from the <c>Microsoft.Playwright</c> package we
/// reference, instead of writing it down.
/// The <c>run-server</c> inside the container has to speak the client's protocol: a version mismatch
/// makes <see cref="IBrowserType.ConnectAsync(string, BrowserTypeConnectOptions)"/> fail with HTTP 428.
/// Microsoft publishes one image per release, tagged <c>v{version}-{os}</c>.
/// Deriving the tag keeps one source of truth in the package version, so when Dependabot updates the
/// client the image follows and the two can never disagree (see ADR-0015).
/// </summary>
internal static class PlaywrightBrowserImage
{
    private const string Repository = "mcr.microsoft.com/playwright";
    private const string OperatingSystemVariant = "noble";

    /// <summary>The browser-server image tag matching the referenced <c>Microsoft.Playwright</c> client.</summary>
    internal static string Tag => BuildTag(ResolveClientVersion());

    /// <summary>
    /// Builds the <c>v{version}-{os}</c> image tag for a client version, dropping any SemVer build
    /// metadata (<c>1.61.0+sha</c> becomes <c>1.61.0</c>) so it matches a tag Microsoft publishes.
    /// </summary>
    internal static string BuildTag(string clientVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);

        string version = clientVersion.Split('+')[0].Trim();

        return $"{Repository}:v{version}-{OperatingSystemVariant}";
    }

    private static string ResolveClientVersion()
    {
        // Microsoft.Playwright ships a placeholder InformationalVersion of "1.0.0". The real release,
        // such as 1.61.0, which is what its published images are tagged with, is in the assembly version.
        // We read that one and not InformationalVersion, so the derived tag follows the package.
        Version version = typeof(IPlaywright).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The Microsoft.Playwright assembly has no version.");

        return version.ToString(3);
    }
}
