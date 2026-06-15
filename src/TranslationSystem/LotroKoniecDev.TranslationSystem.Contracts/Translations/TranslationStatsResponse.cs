namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The mini-dashboard's progress counters (M3-05): a snapshot of the active (non-removed) catalog —
/// how many fragments exist, how many carry Polish, how many are approved for distribution, and how
/// many still need work to reach approval. Counters only, by design (YAGNI — not analytics).
/// <list type="bullet">
/// <item><see cref="Total"/> — every active fragment.</item>
/// <item><see cref="Translated"/> — fragments with Polish content (draft, approved or invalidated).</item>
/// <item><see cref="Approved"/> — fragments approved for the distributed file.</item>
/// <item><see cref="Remaining"/> — active fragments not yet approved (<c>Total - Approved</c>).</item>
/// </list>
/// </summary>
public sealed record TranslationStatsResponse(
    int Total,
    int Translated,
    int Approved,
    int Remaining);
