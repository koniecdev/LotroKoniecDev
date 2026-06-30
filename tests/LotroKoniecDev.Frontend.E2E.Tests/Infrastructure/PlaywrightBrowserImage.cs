using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// Resolves the Playwright browser-server image tag from the referenced <c>Microsoft.Playwright</c>
/// client, instead of hard-coding it. The in-container <c>run-server</c> must speak the client's wire
/// protocol — a version mismatch fails
/// <see cref="IBrowserType.ConnectAsync(string, BrowserTypeConnectOptions)"/> with HTTP 428 — and
/// Microsoft publishes one image per release as <c>v{version}-{os}</c>. Deriving the tag keeps a
/// single source of truth in the package version: when Dependabot bumps the client the image follows
/// automatically, so the two can never drift apart (see ADR-0015).
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
        // Microsoft.Playwright ships a placeholder "1.0.0" InformationalVersion; the real release
        // (e.g. 1.61.0) — the version its published images are tagged with — lives in the assembly
        // version. Key off that, not InformationalVersion, so the derived tag tracks the package.
        Version version = typeof(IPlaywright).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The Microsoft.Playwright assembly has no version.");

        return version.ToString(3);
    }
}
