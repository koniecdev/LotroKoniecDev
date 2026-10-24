using LotroKoniecDev.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace LotroKoniecDev.Frontend.Tests.Integration;

/// <summary>
/// The real Frontend in memory, in Production, as both boxes run it (#1187). The settings a box gets from
/// its .env are set here; they only have to pass the startup checks.
/// </summary>
public class ProductionFrontendFactory : WebApplicationFactory<Program>
{
    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string CallerKey = new('k', 40);

    private readonly string _keyRingPath = Path.Combine(
        Path.GetTempPath(),
        $"lotro-frontend-production-keyring-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting, not ConfigureAppConfiguration: Program.cs reads the keyring path before the host is
        // built, and only a setting passed this way is there in time.
        builder.UseEnvironment("Production");
        builder.UseSetting("DataProtection:KeyRingPath", _keyRingPath);
        builder.UseSetting("TranslationSystem:BaseUrl", "https://tms.staging.invalid/");
        builder.UseSetting("TranslationSystem:CallerKey", CallerKey);
        builder.UseSetting("AuthSystem:BaseUrl", "https://auth.staging.invalid/");
        builder.UseSetting("AuthSystem:Authority", "https://auth.staging.invalid");
        builder.UseSetting("AuthSystem:CallerKey", CallerKey);
    }

    protected override IHost CreateHost(IHostBuilder builder) => HostStartOrder.BuildAndStart(builder);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(_keyRingPath))
        {
            Directory.Delete(_keyRingPath, recursive: true);
        }
    }
}
