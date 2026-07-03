namespace LotroKoniecDev.TranslationSystem.Contracts.Progress;

/// <summary>
/// The public landing-page progress snapshot (#309): the active (non-removed) catalog counters the
/// translator dashboard also shows, plus the game version the catalog is current for. Served
/// anonymously — aggregate counters only, by design.
/// <list type="bullet">
/// <item><see cref="Total"/> — every active fragment.</item>
/// <item><see cref="Translated"/> — fragments with Polish content (draft, approved or invalidated).</item>
/// <item><see cref="Approved"/> — fragments approved for the distributed file.</item>
/// <item><see cref="CurrentGameVersion"/> — the newest <b>processed</b> game version's dotted
/// notation, or <c>null</c> until a first import completes.</item>
/// </list>
/// </summary>
public sealed record PublicProgressResponse(
    int Total,
    int Translated,
    int Approved,
    string? CurrentGameVersion);
