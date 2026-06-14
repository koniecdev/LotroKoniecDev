using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

internal static class DataProtectionDependencyInjectionExtensions
{
    private const string ApplicationName = "LotroKoniecDev.Frontend";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Configures ASP.NET Core Data Protection: a stable application name in every environment (so a
        /// key minted by one instance/path is readable by another), and filesystem key persistence only
        /// when a keyring path is configured. In Development the path is left empty and the framework
        /// default location is used (it already persists on the host). The keyring path is read straight
        /// from configuration at registration time — Data Protection is set up before the DI container is
        /// built, so there is no validated options instance to resolve yet, and binding
        /// <see cref="IServiceProvider"/> here would trip ASP0000.
        /// </summary>
        public IServiceCollection AddFrontendDataProtection(IConfiguration configuration, IHostEnvironment environment)
        {
            DataProtectionSettings settings = configuration
                .GetSection(DataProtectionSettings.ConfigurationSection)
                .Get<DataProtectionSettings>() ?? new DataProtectionSettings();

            // Loud over silent: an ephemeral keyring outside Development is never intended — it silently
            // invalidates every auth cookie / antiforgery token / OIDC correlation cookie on each deploy
            // and makes multi-replica impossible. Fail fast at startup rather than degrade in prod.
            if (!environment.IsDevelopment() && string.IsNullOrWhiteSpace(settings.KeyRingPath))
            {
                throw new InvalidOperationException(
                    $"{DataProtectionSettings.ConfigurationSection}:{nameof(DataProtectionSettings.KeyRingPath)} "
                    + "must be set outside Development. Mount a shared volume and point the keyring at it; "
                    + "otherwise every deploy/scale-out logs all users out.");
            }

            IDataProtectionBuilder builder = services
                .AddDataProtection()
                .SetApplicationName(ApplicationName);

            if (!string.IsNullOrWhiteSpace(settings.KeyRingPath))
            {
                builder.PersistKeysToFileSystem(new DirectoryInfo(settings.KeyRingPath));
            }

            return services;
        }
    }
}
