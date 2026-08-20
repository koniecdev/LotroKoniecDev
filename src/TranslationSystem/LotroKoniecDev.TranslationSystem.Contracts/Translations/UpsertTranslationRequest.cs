namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// Creates or updates the Polish text of one translation row, found by its fragment key
/// <c>(FileId, GossipId)</c> (spec 0001). The translator comes from the authenticated identity and
/// never from the request body.
/// </summary>
public sealed record UpsertTranslationRequest(
    int FileId,
    long GossipId,
    string TranslatedText);
