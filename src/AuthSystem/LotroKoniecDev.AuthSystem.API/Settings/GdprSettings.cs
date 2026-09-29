namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class GdprSettings
{
    public const string ConfigurationSection = "Gdpr";

    public TimeSpan DeletionGracePeriod { get; init; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Once a day on purpose (#780). Every run wakes the Neon compute, which costs about 0.02 CU-h
    /// (ADR-0035). An hourly poll burned about 14 CU-h a month in each environment. The job has a
    /// 14-day deadline, and the account is locked from the moment the deletion is scheduled, so a few
    /// more hours before the erasure change nothing for the user. Do not make it shorter to make it
    /// feel faster. In appsettings.json write one day as <c>1.00:00:00</c>: <c>24:00:00</c> binds to
    /// 24 days.
    /// </summary>
    public TimeSpan DeletionFinalizationPollInterval { get; init; } = TimeSpan.FromDays(1);
}
