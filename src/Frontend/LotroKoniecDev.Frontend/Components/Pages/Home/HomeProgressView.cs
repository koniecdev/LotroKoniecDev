using System.Globalization;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;

namespace LotroKoniecDev.Frontend.Components.Pages.Home;

/// <summary>
/// What the landing page shows, built from the public progress counters (#309): the whole percentages
/// for the two-part progress bar, with the empty-catalog case handled and the second part computed from
/// the already-rounded first one so the two never overlap, plus the numbers formatted with group
/// separators for the tiles.
/// It is kept out of the razor file so the maths and the formatting can be unit-tested without rendering
/// the component, like <see cref="Dashboard.DashboardView"/>.
/// </summary>
internal sealed record HomeProgressView
{
    // A fixed group separator, a non-breaking space as Polish uses, instead of pl-PL's "N0". The
    // culture's separator character differs between ICU versions, which would make the output, and the
    // tests, depend on the machine.
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

    /// <summary>Rows that have Polish and are still waiting for approval (<c>Translated - Approved</c>).</summary>
    public int AwaitingApproval { get; }

    /// <summary>Approved rows as a whole percentage of the active catalog, or <c>0</c> when it is empty.</summary>
    public int ApprovedPercent { get; }

    /// <summary>
    /// The second part of the bar: rows that are translated but not approved, as a whole percentage. It
    /// is the difference between the two rounded percentages, so the two parts together never exceed the
    /// translated share. Rounding each of them on its own could push them over.
    /// </summary>
    public int AwaitingApprovalPercent { get; }

    /// <summary>The dotted notation of the newest processed game version, or <c>null</c> before the first import.</summary>
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
