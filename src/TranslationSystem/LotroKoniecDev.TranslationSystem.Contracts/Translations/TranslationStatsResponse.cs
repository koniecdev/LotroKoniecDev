namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The progress counters on the mini-dashboard (M3-05), taken over the catalog rows that are not
/// removed: how many fragments there are, how many carry Polish, how many are approved for
/// distribution and how many still need work. Counters only, on purpose. This is not analytics.
/// <list type="bullet">
/// <item><see cref="Total"/>: every fragment that is not removed.</item>
/// <item><see cref="Translated"/>: fragments that have Polish, whether draft, approved or invalidated.</item>
/// <item><see cref="Approved"/>: fragments approved for the distributed file.</item>
/// <item><see cref="Remaining"/>: fragments not approved yet (<c>Total - Approved</c>).</item>
/// </list>
/// </summary>
public sealed record TranslationStatsResponse(
    int Total,
    int Translated,
    int Approved,
    int Remaining);
