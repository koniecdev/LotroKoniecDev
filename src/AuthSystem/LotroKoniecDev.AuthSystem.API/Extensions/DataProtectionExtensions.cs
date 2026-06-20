using Microsoft.AspNetCore.DataProtection;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// Configures ASP.NET Core Data Protection for auth-api (M6-04, mirrors the Frontend posture in
/// ADR-0005): a stable application name in every environment (so a key minted by one instance/path
/// is readable by another), and filesystem key persistence only when a keyring path is configured.
/// Data Protection protects the Identity login cookie, Razor antiforgery on the login/account
/// pages, and the Identity password-reset / email-confirmation tokens — all silently invalidated on
/// every deploy (and immediately across replicas) by an ephemeral, process-local keyring.
/// </summary>
internal static class DataProtectionExtensions
{
    private const string ApplicationName = "LotroKoniecDev.AuthSystem";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Pins a stable application name and persists the keyring to the filesystem when a path is
        /// configured. The keyring path is read straight from configuration at registration time —
        /// Data Protection is set up before the DI container is built, so there is no validated
        /// options instance to resolve yet, and binding <see cref="IServiceProvider"/> here would
        /// trip ASP0000. The non-dev guard therefore lives here (not in an
        /// <see cref="Microsoft.Extensions.Options.IValidateOptions{TOptions}"/>) so it fires
        /// before the host is built.
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
    /// Loud over silent: a keyring path is mandatory outside Development/Testing. Development uses
    /// the host-default location (which already persists), and Testing is an in-memory harness with
    /// no real sessions to preserve — both legitimately leave the path empty, mirroring the
    /// CORS startup guard's environment posture. Every deployed environment must mount a shared
    /// volume and point the keyring at it; otherwise each deploy/scale-out logs all users out and
    /// breaks antiforgery + password-reset/email-confirmation links.
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
