namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class GdprSettings
{
    public const string ConfigurationSection = "Gdpr";

    public TimeSpan DeletionGracePeriod { get; init; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Once a day (ADR-0031 amendment): every run wakes the Neon compute, which costs CU-h, and an
    /// erasure falls due only 14 days after the request. Do not make it shorter to make it feel faster.
    /// </summary>
    public TimeSpan DeletionFinalizationPollInterval { get; init; } = TimeSpan.FromDays(1);
}
