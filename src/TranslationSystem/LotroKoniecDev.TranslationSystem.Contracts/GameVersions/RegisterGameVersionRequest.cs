namespace LotroKoniecDev.TranslationSystem.Contracts.GameVersions;

/// <summary>
/// Registers a game version by hand, which is the fallback for when reading the forum stops working
/// (spec 0001). The value is the dotted LOTRO notation, for example <c>48.0</c>.
/// </summary>
public sealed record RegisterGameVersionRequest(string Version);
