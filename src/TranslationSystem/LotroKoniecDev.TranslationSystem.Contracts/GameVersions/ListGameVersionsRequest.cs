namespace LotroKoniecDev.TranslationSystem.Contracts.GameVersions;

/// <summary>
/// The game-version list's query option (#1478). No client sends <see cref="Sort"/> yet, so the record has
/// no method that builds the address; the first client adds one, as <c>ListTranslationsRequest</c> has.
/// </summary>
public sealed record ListGameVersionsRequest(string? Sort = null);
