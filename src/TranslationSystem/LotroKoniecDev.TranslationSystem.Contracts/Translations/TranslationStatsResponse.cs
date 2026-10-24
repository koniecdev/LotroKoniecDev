namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The mini-dashboard counters (M3-05), over the rows that are not removed. Counters only; this is not
/// analytics.
/// <list type="bullet">
/// <item><see cref="Translated"/>: rows that have Polish, whether draft, approved or invalidated.</item>
/// <item><see cref="Remaining"/>: <c>Total - Approved</c>.</item>
/// </list>
/// </summary>
public sealed record TranslationStatsResponse(
    int Total,
    int Translated,
    int Approved,
    int Remaining);
