namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// The base address of the TMS API (<c>TranslationSystem.API</c>) the Frontend calls over HTTP.
/// </summary>
internal sealed class TranslationSystemSettings
{
    public const string ConfigurationSection = "TranslationSystem";

    public required string BaseUrl { get; init; }
}
