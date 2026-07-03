using LotroKoniecDev.Frontend.Components.Pages.Home;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Home;

public sealed class HomeProgressViewTests
{
    [Fact]
    public void From_CopiesEveryCounterAndTheVersionThrough()
    {
        PublicProgressResponse progress = new(Total: 200, Translated: 150, Approved: 80, CurrentGameVersion: "48.1");

        HomeProgressView view = HomeProgressView.From(progress);

        view.Total.ShouldBe(200);
        view.Translated.ShouldBe(150);
        view.Approved.ShouldBe(80);
        view.AwaitingApproval.ShouldBe(70);
        view.CurrentGameVersion.ShouldBe("48.1");
    }

    [Theory]
    [InlineData(0, 0, 0)]       // empty catalog — no division by zero
    [InlineData(100, 0, 0)]     // nothing approved yet
    [InlineData(100, 100, 100)] // all approved
    [InlineData(200, 100, 50)]  // exact half
    [InlineData(3, 1, 33)]      // 33.33% rounds down
    [InlineData(3, 2, 67)]      // 66.67% rounds up
    [InlineData(8, 1, 13)]      // 12.5% rounds away from zero to 13
    public void From_ComputesApprovedPercent(int total, int approved, int expectedPercent)
    {
        PublicProgressResponse progress = new(total, Translated: approved, approved, CurrentGameVersion: null);

        HomeProgressView view = HomeProgressView.From(progress);

        view.ApprovedPercent.ShouldBe(expectedPercent);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]        // empty catalog
    [InlineData(100, 60, 25, 35)]   // 60% translated, 25% approved → 35% awaiting
    [InlineData(100, 40, 40, 0)]    // everything translated is already approved
    [InlineData(3, 2, 1, 34)]       // 67% − 33% — derived from the rounded pair, so segments tile exactly
    public void From_DerivesTheAwaitingSegmentFromTheRoundedPercentages(
        int total, int translated, int approved, int expectedAwaitingPercent)
    {
        PublicProgressResponse progress = new(total, translated, approved, CurrentGameVersion: null);

        HomeProgressView view = HomeProgressView.From(progress);

        view.AwaitingApprovalPercent.ShouldBe(expectedAwaitingPercent);
        (view.ApprovedPercent + view.AwaitingApprovalPercent).ShouldBeLessThanOrEqualTo(100);
    }

    [Fact]
    public void From_FormatsTheCountersWithNonBreakingSpaceGroups()
    {
        // The fixed NBSP separator keeps the rendering identical across ICU versions.
        PublicProgressResponse progress = new(Total: 1234567, Translated: 12345, Approved: 999, CurrentGameVersion: null);

        HomeProgressView view = HomeProgressView.From(progress);

        view.TotalDisplay.ShouldBe("1 234 567");
        view.TranslatedDisplay.ShouldBe("12 345");
        view.ApprovedDisplay.ShouldBe("999");
        view.AwaitingApprovalDisplay.ShouldBe("11 346");
    }

    [Fact]
    public void From_WithNoProcessedVersion_KeepsTheVersionNull()
    {
        PublicProgressResponse progress = new(Total: 1, Translated: 0, Approved: 0, CurrentGameVersion: null);

        HomeProgressView view = HomeProgressView.From(progress);

        view.CurrentGameVersion.ShouldBeNull();
    }

    [Fact]
    public void From_WhenProgressNull_Throws()
    {
        Should.Throw<ArgumentNullException>(() => HomeProgressView.From(null!));
    }
}
