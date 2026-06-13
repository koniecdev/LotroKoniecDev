namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// Creates or updates the Polish content of one translation row, identified by its stable fragment
/// key <c>(FileId, GossipId)</c> (spec 0001). The submitting translator is taken from the
/// authenticated identity, never the request body.
/// </summary>
public sealed record UpsertTranslationRequest(
    int FileId,
    long GossipId,
    string TranslatedText);
