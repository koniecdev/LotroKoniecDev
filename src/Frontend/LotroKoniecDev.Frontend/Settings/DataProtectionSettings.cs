namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// Filesystem directory the Data Protection keyring is persisted to (mounted to a shared
/// volume in container deployments). Empty is valid in Development: the framework default
/// location at <c>~/.aspnet/DataProtection-Keys</c> already persists across host restarts, so
/// dev must not hardcode a container path. A non-dev startup guard rejects an empty value (an
/// ephemeral keyring in production mass-logs-out users on every deploy because every auth cookie
/// / antiforgery token / OIDC correlation cookie becomes unreadable).
/// </summary>
internal sealed class DataProtectionSettings
{
    public const string ConfigurationSection = "DataProtection";

    public string? KeyRingPath { get; init; }
}
