namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// The base address of the TMS API (<c>TranslationSystem.API</c>) the Frontend calls over HTTP.
/// </summary>
internal sealed class TranslationSystemSettings
{
    public const string ConfigurationSection = "TranslationSystem";

    public required string BaseUrl { get; init; }

    /// <summary>
    /// The environment's shared key, sent next to the visitor's address so the TMS API meters each call
    /// on that visitor instead of on this container (ADR-0054, #823). Compose feeds the TMS API the same
    /// <c>FRONTEND_CALLER_KEY</c>.
    /// </summary>
    public string? CallerKey { get; init; }
}
