namespace LotroKoniecDev.Frontend.Settings;

internal sealed class TranslationSystemSettings
{
    public const string ConfigurationSection = "TranslationSystem";

    public required string BaseUrl { get; init; }

    /// <summary>
    /// Sent next to the visitor's address, so the TMS API meters each call on that visitor instead of on
    /// this container (ADR-0054). Compose feeds the TMS API the same <c>FRONTEND_CALLER_KEY</c>.
    /// </summary>
    public string? CallerKey { get; init; }
}
