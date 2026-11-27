namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The row comes from the address and the rows to fill are found by the server at the moment of the save,
/// so the client never sends a list of rows (#1587).
/// </summary>
public sealed record FillIdenticalTranslationsRequest(string TranslatedText);
