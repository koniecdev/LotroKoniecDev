namespace LotroKoniecDev.AuthSystem.Persistence.Settings;

public sealed class ConnectionStringSettings
{
    public const string ConfigurationSection = "ConnectionStrings";

    public required string AuthDatabase { get; init; }
}
