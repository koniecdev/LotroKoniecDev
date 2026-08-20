namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// The directory the Data Protection keyring is written to. In container deployments it is a shared
/// volume.
/// It may be empty in Development, because the framework's default location,
/// <c>~/.aspnet/DataProtection-Keys</c>, already survives a restart, and dev must not hardcode a
/// container path. Outside development a startup check rejects an empty value: a keyring that lives only
/// in one process logs every user out on each deploy, because no auth cookie, antiforgery token or OIDC
/// correlation cookie can be read afterwards.
/// </summary>
internal sealed class DataProtectionSettings
{
    public const string ConfigurationSection = "DataProtection";

    public string? KeyRingPath { get; init; }
}
