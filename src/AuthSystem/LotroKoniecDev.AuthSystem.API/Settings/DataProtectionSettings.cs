namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// The keyring directory, a volume in containers. Required outside Development and Testing
/// (<c>DataProtectionExtensions.GuardKeyRingPath</c>).
/// </summary>
internal sealed class DataProtectionSettings
{
    public const string ConfigurationSection = "DataProtection";

    public string? KeyRingPath { get; init; }
}
