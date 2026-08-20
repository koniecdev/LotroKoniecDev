using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Components.Pages.Dashboard;

/// <summary>
/// What the mini-dashboard shows, built from the API counters (M3-05): the four counters as they are,
/// plus the percentage the progress bar draws. It is kept out of the razor file so the percentage
/// maths, above all the empty-catalog case and the rounding, can be unit-tested without rendering the
/// component.
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

    /// <summary>Approved rows as a whole percentage of the active catalog, or <c>0</c> when it is empty.</summary>
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
