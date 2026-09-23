namespace LotroKoniecDev.TranslationSystem.API.Settings;

/// <summary>
/// The per-environment key the frontend presents next to a forwarded visitor address (ADR-0054, #823).
/// The box <c>.env</c> carries it as <c>FRONTEND_CALLER_KEY</c>, and compose hands the same value to the
/// frontend, the auth API and this API, so the copies cannot drift.
/// </summary>
internal sealed class FrontendCallerSettings
{
    public const string ConfigurationSection = "FrontendCaller";

    public string? Key { get; init; }
}
