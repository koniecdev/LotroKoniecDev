namespace LotroKoniecDev.TranslationSystem.Contracts.GameVersions;

/// <summary>
/// Manually registers a game version — the degenerate fallback for when the forum scrape breaks
/// (spec 0001): the dotted LOTRO notation string, e.g. <c>48.0</c>.
/// </summary>
public sealed record RegisterGameVersionRequest(string Version);
