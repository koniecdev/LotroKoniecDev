using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Components.Pages.Dashboard;

/// <summary>
/// The mini-dashboard's view state derived from the API counters (M3-05): the four counters as-is plus
/// the approval-progress percentage the progress bar renders. Pure and isolated from the razor so the
/// percent math — chiefly the zero-catalog guard and rounding — is unit-testable without rendering the
/// component (the Frontend has no bUnit).
/// </summary>
internal sealed record DashboardView
{
    private DashboardView(int total, int translated, int approved, int remaining, int approvedPercent)
    {
        Total = total;
        Translated = translated;
        Approved = approved;
        Remaining = remaining;
        ApprovedPercent = approvedPercent;
    }

    public int Total { get; }

    public int Translated { get; }

    public int Approved { get; }

    public int Remaining { get; }

    /// <summary>Approved as a whole-number percentage of the active catalog; <c>0</c> when empty.</summary>
    public int ApprovedPercent { get; }

    public static DashboardView From(TranslationStatsResponse stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        return new DashboardView(
            stats.Total,
            stats.Translated,
            stats.Approved,
            stats.Remaining,
            Percent(stats.Approved, stats.Total));
    }

    private static int Percent(int part, int whole)
    {
        if (whole <= 0)
        {
            return 0;
        }

        return (int)Math.Round(part / (double)whole * 100, MidpointRounding.AwayFromZero);
    }
}
