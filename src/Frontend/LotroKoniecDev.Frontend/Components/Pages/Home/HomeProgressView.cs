using System.Globalization;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;

namespace LotroKoniecDev.Frontend.Components.Pages.Home;

/// <summary>
/// The landing page's view state derived from the public progress counters (#309): the two-segment
/// approval meter's whole-number percentages (zero-catalog guarded; the segments are derived from the
/// already-rounded percentages so they always tile without overlap) plus grouped number strings for
/// the stat tiles. Pure and isolated from the razor so the math and formatting are unit-testable
/// without rendering the component (mirrors <see cref="Dashboard.DashboardView"/>).
/// </summary>
internal sealed record HomeProgressView
{
    // A fixed group separator (NBSP, Polish convention) instead of pl-PL's "N0": the culture's
    // separator character differs across ICU versions, which would make the rendering (and its
    // tests) environment-dependent.
    private static readonly NumberFormatInfo GroupedFormat = new()
    {
        NumberGroupSeparator = " ",
        NumberGroupSizes = [3]
    };

    private HomeProgressView(
        int total,
        int translated,
        int approved,
        int awaitingApproval,
        int approvedPercent,
        int awaitingApprovalPercent,
        string? currentGameVersion)
    {
        Total = total;
        Translated = translated;
        Approved = approved;
        AwaitingApproval = awaitingApproval;
        ApprovedPercent = approvedPercent;
        AwaitingApprovalPercent = awaitingApprovalPercent;
        CurrentGameVersion = currentGameVersion;
    }

    public int Total { get; }

    public int Translated { get; }

    public int Approved { get; }

    /// <summary>Rows carrying Polish that still await approval (<c>Translated - Approved</c>).</summary>
    public int AwaitingApproval { get; }

    /// <summary>Approved as a whole-number percentage of the active catalog; <c>0</c> when empty.</summary>
    public int ApprovedPercent { get; }

    /// <summary>
    /// The meter's second segment: translated-but-unapproved as a whole-number percentage. Derived as
    /// the difference of the two rounded percentages, so both segments never sum past the translated
    /// share (rounding each side independently could).
    /// </summary>
    public int AwaitingApprovalPercent { get; }

    /// <summary>The newest processed game version's dotted notation, or <c>null</c> before a first import.</summary>
    public string? CurrentGameVersion { get; }

    public string TotalDisplay => Format(Total);

    public string TranslatedDisplay => Format(Translated);

    public string ApprovedDisplay => Format(Approved);

    public string AwaitingApprovalDisplay => Format(AwaitingApproval);

    public static HomeProgressView From(PublicProgressResponse progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        int approvedPercent = Percent(progress.Approved, progress.Total);
        int translatedPercent = Percent(progress.Translated, progress.Total);

        return new HomeProgressView(
            progress.Total,
            progress.Translated,
            progress.Approved,
            progress.Translated - progress.Approved,
            approvedPercent,
            translatedPercent - approvedPercent,
            progress.CurrentGameVersion);
    }

    private static int Percent(int part, int whole)
    {
        if (whole <= 0)
        {
            return 0;
        }

        return (int)Math.Round(part / (double)whole * 100, MidpointRounding.AwayFromZero);
    }

    private static string Format(int value) => value.ToString("#,0", GroupedFormat);
}
