namespace LotroKoniecDev.TranslationSystem.Contracts.Progress;

/// <summary>
/// The public landing-page numbers (#309): the dashboard's counters plus the version the catalog
/// matches. Anyone can read it, so it carries totals only.
/// <list type="bullet">
/// <item><see cref="Translated"/>: rows that have Polish, whether draft, approved or invalidated.</item>
/// <item><see cref="CurrentGameVersion"/>: the newest processed version, or <c>null</c> until the first
/// import finishes.</item>
/// </list>
/// </summary>
public sealed record PublicProgressResponse(
    int Total,
    int Translated,
    int Approved,
    string? CurrentGameVersion);
