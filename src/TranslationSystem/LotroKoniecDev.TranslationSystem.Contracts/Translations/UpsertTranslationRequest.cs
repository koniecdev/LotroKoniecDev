namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The translator comes from the authenticated identity, never from the request body (spec 0001).
/// </summary>
public sealed record UpsertTranslationRequest(
    int FileId,
    long GossipId,
    string TranslatedText);
