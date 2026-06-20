namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// Filesystem directory the ASP.NET Core Data Protection keyring is persisted to (mounted to a
/// shared volume in container deployments). Data Protection protects the Identity login cookie
/// (<c>LotroKoniecDev.Auth</c>), the Razor antiforgery tokens on the login/account pages, and the
/// Identity data-protector tokens (email-confirmation / password-reset links). Empty is valid in
/// Development: the framework default location at <c>~/.aspnet/DataProtection-Keys</c> already
/// persists across host restarts, so dev must not hardcode a container path. A non-dev startup
/// guard rejects an empty value — an ephemeral keyring in a deployed environment mass-logs-out
/// users and breaks antiforgery + reset/confirm links on every deploy (and immediately with more
/// than one replica), because every protected payload becomes unreadable.
/// </summary>
internal sealed class DataProtectionSettings
{
    public const string ConfigurationSection = "DataProtection";

    public string? KeyRingPath { get; init; }
}
