using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LotroKoniecDev.Frontend.Tests.Integration;

/// <summary>
/// Boots the real Frontend in memory, in Staging: outside Development and Testing both caller keys are
/// required, like on a deployed box. The settings a box gets from its .env are set here, with a keyring
/// folder of its own. The values only have to pass the startup checks, because nothing calls out
/// during these tests.
/// </summary>
public sealed class StagingFrontendFactory : WebApplicationFactory<Program>
{
    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string CallerKey = new('k', 40);

    private readonly string _keyRingPath = Path.Combine(
        Path.GetTempPath(),
        $"lotro-frontend-staging-keyring-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting, not ConfigureAppConfiguration: Program.cs reads the keyring path before the host is
        // built, and only a setting passed this way is there in time.
        builder.UseEnvironment("Staging");
        builder.UseSetting("DataProtection:KeyRingPath", _keyRingPath);
        builder.UseSetting("TranslationSystem:BaseUrl", "https://tms.staging.invalid/");
        builder.UseSetting("TranslationSystem:CallerKey", CallerKey);
        builder.UseSetting("AuthSystem:BaseUrl", "https://auth.staging.invalid/");
        builder.UseSetting("AuthSystem:Authority", "https://auth.staging.invalid");
        builder.UseSetting("AuthSystem:CallerKey", CallerKey);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(_keyRingPath))
        {
            Directory.Delete(_keyRingPath, recursive: true);
        }
    }
}
