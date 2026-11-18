using Avalonia;

namespace LotroKoniecDev.DesktopApp.Presentation.Views;

/// <summary>
/// Keeps a window inside the work area of its screen, the screen without the taskbar (#1463). The work
/// area, the top and the frame height are physical pixels; the height limit is in the window's own units.
/// </summary>
internal static class ScreenFit
{
    /// <summary>Returns null when the title bar alone fills the work area, so no limit makes sense.</summary>
    public static double? MaxClientHeight(PixelRect workArea, double desktopScaling, double titleBarHeight)
    {
        double maxClientHeight = workArea.Height / desktopScaling - titleBarHeight;
        return maxClientHeight > 0 ? maxClientHeight : null;
    }

    /// <summary>
    /// Moves the top up only as far as the bottom needs. A frame taller than the work area keeps its
    /// title bar on screen.
    /// </summary>
    public static int FitTop(PixelRect workArea, int top, int frameHeight) =>
        Math.Max(workArea.Y, Math.Min(top, workArea.Bottom - frameHeight));
}
