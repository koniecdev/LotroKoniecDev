namespace LotroKoniecDev.TranslationSystem.Contracts.Progress;

/// <summary>
/// The progress numbers on the public landing page (#309): the same counters over the active catalog
/// that the translator dashboard shows, plus the game version the catalog matches. Anyone can read
/// it, so it carries totals only, on purpose.
/// <list type="bullet">
/// <item><see cref="Total"/>: every fragment that is not removed.</item>
/// <item><see cref="Translated"/>: fragments that have Polish, whether draft, approved or invalidated.</item>
/// <item><see cref="Approved"/>: fragments approved for the distributed file.</item>
/// <item><see cref="CurrentGameVersion"/>: the dotted notation of the newest <b>processed</b> game
/// version, or <c>null</c> until the first import finishes.</item>
/// </list>
/// </summary>
public sealed record PublicProgressResponse(
    int Total,
    int Translated,
    int Approved,
    string? CurrentGameVersion);
