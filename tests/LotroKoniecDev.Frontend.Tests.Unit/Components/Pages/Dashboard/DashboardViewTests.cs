using LotroKoniecDev.Frontend.Components.Pages.Dashboard;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Dashboard;

public sealed class DashboardViewTests
{
    [Fact]
    public void From_CopiesEveryCounterThroughUnchanged()
    {
        TranslationStatsResponse stats = new(Total: 200, Translated: 150, Approved: 80, Remaining: 120);

        DashboardView view = DashboardView.From(stats);

        view.Total.ShouldBe(200);
        view.Translated.ShouldBe(150);
        view.Approved.ShouldBe(80);
        view.Remaining.ShouldBe(120);
    }

    [Theory]
    [InlineData(0, 0, 0)]      // empty catalog — no division by zero
    [InlineData(100, 0, 0)]    // nothing approved yet
    [InlineData(100, 100, 100)] // all approved
    [InlineData(200, 100, 50)] // exact half
    [InlineData(3, 1, 33)]     // 33.33% rounds down
    [InlineData(3, 2, 67)]     // 66.67% rounds up
    [InlineData(8, 1, 13)]     // 12.5% rounds away from zero to 13
    public void From_ComputesApprovedPercent(int total, int approved, int expectedPercent)
    {
        TranslationStatsResponse stats = new(total, Translated: approved, approved, Remaining: total - approved);

        DashboardView view = DashboardView.From(stats);

        view.ApprovedPercent.ShouldBe(expectedPercent);
    }

    [Fact]
    public void From_WhenStatsNull_Throws()
    {
        Should.Throw<ArgumentNullException>(() => DashboardView.From(null!));
    }
}
