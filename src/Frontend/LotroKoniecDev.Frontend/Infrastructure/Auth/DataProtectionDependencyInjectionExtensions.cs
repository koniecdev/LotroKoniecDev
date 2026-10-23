using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

internal static class DataProtectionDependencyInjectionExtensions
{
    private const string ApplicationName = "LotroKoniecDev.Frontend";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// A fixed application name and a kept keyring, so cookies, antiforgery tokens and the OIDC
        /// correlation survive a restart and work across instances. The path is read straight from
        /// configuration, because this runs before the DI container exists (ASP0000).
        /// </summary>
        public IServiceCollection AddFrontendDataProtection(IConfiguration configuration, IHostEnvironment environment)
        {
            DataProtectionSettings settings = configuration
                .GetSection(DataProtectionSettings.ConfigurationSection)
                .Get<DataProtectionSettings>() ?? new DataProtectionSettings();

            // A keyring that lives in one process silently logs every user out on each deploy, so stop at
            // startup instead.
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
