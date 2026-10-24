namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// <see cref="KeyRingPath"/> is a shared volume in containers. It may be empty in Development, where the
/// framework's default location already survives a restart; elsewhere a startup check rejects an empty
/// value.
/// </summary>
internal sealed class DataProtectionSettings
{
    public const string ConfigurationSection = "DataProtection";

    public string? KeyRingPath { get; init; }
}
