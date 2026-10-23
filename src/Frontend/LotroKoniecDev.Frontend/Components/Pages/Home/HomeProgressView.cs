using System.Globalization;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;

namespace LotroKoniecDev.Frontend.Components.Pages.Home;

/// <summary>
/// The landing page's numbers from the public progress counters (#309). Kept out of the razor file so
/// the maths and the formatting can be unit-tested without rendering.
/// </summary>
internal sealed record HomeProgressView
{
    // A fixed non-breaking space instead of pl-PL's "N0": the culture's separator differs between ICU
    // versions, which would make the output depend on the machine.
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

    /// <summary><c>Translated - Approved</c>.</summary>
    public int AwaitingApproval { get; }

    /// <summary><c>0</c> when the catalog is empty.</summary>
    public int ApprovedPercent { get; }

    /// <summary>
    /// The difference between the two rounded percentages, so the two bar parts never exceed the
    /// translated share. Rounding each on its own could push them over.
    /// </summary>
    public int AwaitingApprovalPercent { get; }

    /// <summary>The newest processed game version, or <c>null</c> before the first import.</summary>
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
