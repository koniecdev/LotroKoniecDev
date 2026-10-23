namespace LotroKoniecDev.TranslationSystem.Contracts.GameVersions;

/// <summary>
/// Registering a version by hand is the normal update routine (ADR-0030). The value is the dotted
/// notation, for example <c>48.0</c>.
/// </summary>
public sealed record RegisterGameVersionRequest(string Version);
