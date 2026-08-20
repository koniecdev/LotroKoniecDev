using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

internal static class DataProtectionDependencyInjectionExtensions
{
    private const string ApplicationName = "LotroKoniecDev.Frontend";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Sets up ASP.NET Core Data Protection: the same application name in every environment, so a key
        /// one instance created can be read by another, and keys written to disk only when a keyring path
        /// is configured. In Development the path stays empty and the framework's default location is
        /// used, which already survives a restart.
        /// The path is read straight from configuration here, because Data Protection is set up before the
        /// DI container exists: there is no validated options object yet, and resolving
        /// <see cref="IServiceProvider"/> at this point would trigger ASP0000.
        /// </summary>
        public IServiceCollection AddFrontendDataProtection(IConfiguration configuration, IHostEnvironment environment)
        {
            DataProtectionSettings settings = configuration
                .GetSection(DataProtectionSettings.ConfigurationSection)
                .Get<DataProtectionSettings>() ?? new DataProtectionSettings();

            // Better to fail loudly than to fail quietly. A keyring that lives only in one process is
            // never what anyone wants outside Development: it invalidates every auth cookie, antiforgery
            // token and OIDC correlation cookie on each deploy, without a word, and makes more than one
            // replica impossible. Stop at startup instead of running badly in production.
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
