namespace LotroKoniecDev.TranslationSystem.API.Settings;

/// <summary>
/// Presented by the frontend next to a forwarded visitor address (ADR-0054). Compose hands the box's
/// <c>FRONTEND_CALLER_KEY</c> to the frontend and both APIs, so the copies cannot drift.
/// </summary>
internal sealed class FrontendCallerSettings
{
    public const string ConfigurationSection = "FrontendCaller";

    public string? Key { get; init; }
}
