using Microsoft.AspNetCore.DataProtection;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// Sets up ASP.NET Core Data Protection for auth-api (M6-04, the same approach the Frontend takes in
/// ADR-0005): the same application name in every environment, so a key one instance created can be
/// read by another, and keys written to disk only when a keyring path is configured.
/// Data Protection protects the Identity login cookie, the Razor antiforgery tokens on the login and
/// account pages, and the Identity password-reset and e-mail-confirmation tokens. A keyring that lives
/// only in one process invalidates all of them on every deploy, and at once across replicas, without
/// any error message.
/// </summary>
internal static class DataProtectionExtensions
{
    private const string ApplicationName = "LotroKoniecDev.AuthSystem";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Fixes the application name and writes the keyring to disk when a path is configured.
        /// The path is read straight from configuration here, because Data Protection is set up before
        /// the DI container exists: there is no validated options object yet, and resolving
        /// <see cref="IServiceProvider"/> at this point would trigger ASP0000.
        /// So the check for non-development environments lives here and not in an
        /// <see cref="Microsoft.Extensions.Options.IValidateOptions{TOptions}"/>, and it runs before
        /// the host is built.
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
    /// Better to fail loudly than to fail quietly: a keyring path is required outside Development and
    /// Testing. Development uses the host's default location, which already keeps the keys, and Testing
    /// runs in memory with no real sessions to keep. Both may leave the path empty, the same way the
    /// CORS startup check treats those environments.
    /// Every deployed environment must mount a shared volume and point the keyring at it. Without that,
    /// each deploy or new replica logs every user out and breaks antiforgery, password-reset and
    /// e-mail-confirmation links.
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
