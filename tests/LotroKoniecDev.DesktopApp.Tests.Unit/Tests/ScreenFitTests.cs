using Avalonia;
using LotroKoniecDev.DesktopApp.Presentation.Views;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

public sealed class ScreenFitTests
{
    [Theory]
    [InlineData(690, 1.0, 0, 690)]
    [InlineData(1035, 1.5, 0, 690)]
    [InlineData(690, 1.0, 30, 660)]
    [InlineData(1008, 1.5, 31, 641)]
    public void MaxClientHeight_ForAWorkArea_TakesTheTitleBarOffItsHeightInWindowUnits(
        int workAreaHeight, double scaling, double titleBarHeight, double expected)
    {
        double? maxHeight = ScreenFit.MaxClientHeight(new PixelRect(0, 0, 1920, workAreaHeight), scaling, titleBarHeight);

        maxHeight.ShouldNotBeNull().ShouldBe(expected, 0.001);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(20, 30)]
    [InlineData(0, 0)]
    public void MaxClientHeight_WhenTheTitleBarFillsTheWorkArea_ReturnsNoLimit(int workAreaHeight, double titleBarHeight)
    {
        double? maxHeight = ScreenFit.MaxClientHeight(new PixelRect(0, 0, 1920, workAreaHeight), 1.0, titleBarHeight);

        maxHeight.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 100, 400)]
    [InlineData(0, 0, 690)]
    [InlineData(690, 800, 500)]
    public void FitTop_WhenTheFrameFits_KeepsTheTop(int workAreaTop, int top, int frameHeight)
    {
        int fitted = ScreenFit.FitTop(new PixelRect(0, workAreaTop, 1920, 690), top, frameHeight);

        fitted.ShouldBe(top);
    }

    [Theory]
    [InlineData(0, 392, 496, 194)]
    [InlineData(0, 600, 690, 0)]
    [InlineData(690, 900, 584, 796)]
    public void FitTop_WhenTheBottomIsBelowTheWorkArea_MovesUpOnlyAsFarAsNeeded(
        int workAreaTop, int top, int frameHeight, int expected)
    {
        int fitted = ScreenFit.FitTop(new PixelRect(0, workAreaTop, 1920, 690), top, frameHeight);

        fitted.ShouldBe(expected);
    }

    [Theory]
    [InlineData(0, -50, 300)]
    [InlineData(690, 194, 584)]
    public void FitTop_WhenTheTopIsAboveTheWorkArea_MovesDownToIt(int workAreaTop, int top, int frameHeight)
    {
        int fitted = ScreenFit.FitTop(new PixelRect(0, workAreaTop, 1920, 690), top, frameHeight);

        fitted.ShouldBe(workAreaTop);
    }

    [Theory]
    [InlineData(0, 100, 900)]
    [InlineData(690, 1000, 955)]
    public void FitTop_WhenTheFrameIsTallerThanTheWorkArea_KeepsTheTitleBarOnScreen(int workAreaTop, int top, int frameHeight)
    {
        int fitted = ScreenFit.FitTop(new PixelRect(0, workAreaTop, 1920, 690), top, frameHeight);

        fitted.ShouldBe(workAreaTop);
    }
}
