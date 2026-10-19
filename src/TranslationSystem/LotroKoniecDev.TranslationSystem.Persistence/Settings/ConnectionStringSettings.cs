namespace LotroKoniecDev.TranslationSystem.Persistence.Settings;

public sealed class ConnectionStringSettings
{
    public const string ConfigurationSection = "ConnectionStrings";

    public const string TranslationDatabaseKey = $"{ConfigurationSection}:{nameof(TranslationDatabase)}";

    public required string TranslationDatabase { get; init; }
}
