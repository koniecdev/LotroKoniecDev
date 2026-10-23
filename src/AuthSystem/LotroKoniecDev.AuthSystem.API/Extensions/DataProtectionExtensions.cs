using Microsoft.AspNetCore.DataProtection;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// The same application name everywhere, so one instance can read another's keys, and keys on disk
/// when a keyring path is configured (ADR-0005).
/// </summary>
internal static class DataProtectionExtensions
{
    private const string ApplicationName = "LotroKoniecDev.AuthSystem";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Reads the path straight from configuration, because Data Protection is set up before the DI
        /// container exists, so the check cannot be an
        /// <see cref="Microsoft.Extensions.Options.IValidateOptions{TOptions}"/>.
        /// </summary>
        public IServiceCollection AddAuthDataProtection(IConfiguration configuration, IHostEnvironment environment)
        {
            DataProtectionSettings settings = configuration
                .GetSection(DataProtectionSettings.ConfigurationSection)
                .Get<DataProtectionSettings>() ?? new DataProtectionSettings();

            GuardKeyRingPath(settings, environment);

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

    /// <summary>
    /// A keyring that lives in one process silently logs every user out and breaks antiforgery and the
    /// e-mailed links on every deploy. Development keeps keys in the host's default location and Testing
    /// has no sessions to keep, so both may leave the path empty.
    /// </summary>
    public static void GuardKeyRingPath(DataProtectionSettings settings, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment() || environment.IsTesting())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.KeyRingPath))
        {
            throw new InvalidOperationException(
                $"{DataProtectionSettings.ConfigurationSection}:{nameof(DataProtectionSettings.KeyRingPath)} "
                + $"must be set in {environment.EnvironmentName}. Mount a shared/persistent volume and point the "
                + "keyring at it (e.g. DataProtection__KeyRingPath=/keys); otherwise every deploy/scale-out logs "
                + "all users out and breaks antiforgery + password-reset/email-confirmation links.");
        }
    }
}
