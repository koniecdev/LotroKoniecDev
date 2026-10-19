namespace LotroKoniecDev.AuthSystem.Persistence.Settings;

public sealed class ConnectionStringSettings
{
    public const string ConfigurationSection = "ConnectionStrings";

    public const string AuthDatabaseKey = $"{ConfigurationSection}:{nameof(AuthDatabase)}";

    public required string AuthDatabase { get; init; }
}
