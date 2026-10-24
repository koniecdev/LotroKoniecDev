using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// The image tag comes from the referenced <c>Microsoft.Playwright</c> package, so a client update moves the
/// image with it (ADR-0015). A version mismatch makes
/// <see cref="IBrowserType.ConnectAsync(string, BrowserTypeConnectOptions)"/> fail with HTTP 428.
/// </summary>
internal static class PlaywrightBrowserImage
{
    private const string Repository = "mcr.microsoft.com/playwright";
    private const string OperatingSystemVariant = "noble";

    internal static string Tag => BuildTag(ResolveClientVersion());

    /// <summary>
    /// Drops SemVer build metadata (<c>1.61.0+sha</c> becomes <c>1.61.0</c>) to match a published tag.
    /// </summary>
    internal static string BuildTag(string clientVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);

        string version = clientVersion.Split('+')[0].Trim();

        return $"{Repository}:v{version}-{OperatingSystemVariant}";
    }

    private static string ResolveClientVersion()
    {
        // InformationalVersion is a "1.0.0" placeholder; the real release is in the assembly version.
        Version version = typeof(IPlaywright).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The Microsoft.Playwright assembly has no version.");

        return version.ToString(3);
    }
}
