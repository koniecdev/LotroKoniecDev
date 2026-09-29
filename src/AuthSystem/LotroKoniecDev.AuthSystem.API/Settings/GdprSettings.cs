namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class GdprSettings
{
    public const string ConfigurationSection = "Gdpr";

    public TimeSpan DeletionGracePeriod { get; init; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Once a day on purpose (#780, ADR-0031 amendment). Every run wakes the Neon compute, and each
    /// wake-up costs CU-h (ADR-0035). An erasure only falls due 14 days after the request, and GDPR
    /// allows a month, so a few more hours change nothing for the user. Do not make it shorter to make
    /// it feel faster. In appsettings.json write one day as <c>1.00:00:00</c>: <c>24:00:00</c> binds
    /// to 24 days.
    /// </summary>
    public TimeSpan DeletionFinalizationPollInterval { get; init; } = TimeSpan.FromDays(1);
}
