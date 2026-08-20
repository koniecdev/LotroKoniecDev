namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// The directory the ASP.NET Core Data Protection keyring is written to. In container deployments it
/// is a shared volume.
/// Data Protection protects the Identity login cookie (<c>LotroKoniecDev.Auth</c>), the Razor
/// antiforgery tokens on the login and account pages, and the Identity tokens in the
/// e-mail-confirmation and password-reset links.
/// It may be empty in Development, because the framework's default location,
/// <c>~/.aspnet/DataProtection-Keys</c>, already survives a restart, and dev must not hardcode a
/// container path. Outside development a startup check rejects an empty value: a keyring that lives
/// only in one process logs every user out and breaks antiforgery and the reset and confirm links on
/// every deploy, and at once when there is more than one replica, because nothing can read what was
/// protected before.
/// </summary>
internal sealed class DataProtectionSettings
{
    public const string ConfigurationSection = "DataProtection";

    public string? KeyRingPath { get; init; }
}
