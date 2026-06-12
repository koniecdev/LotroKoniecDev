namespace LotroKoniecDev.TranslationSystem.Persistence.Settings;

public sealed class ConnectionStringSettings
{
    public const string ConfigurationSection = "ConnectionStrings";

    public required string TranslationDatabase { get; init; }
}
