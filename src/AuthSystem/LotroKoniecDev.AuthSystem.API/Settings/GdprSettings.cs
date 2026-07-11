namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class GdprSettings
{
    public const string ConfigurationSection = "Gdpr";

    public TimeSpan DeletionGracePeriod { get; init; } = TimeSpan.FromDays(14);
    public TimeSpan DeletionFinalizationPollInterval { get; init; } = TimeSpan.FromHours(1);
}
