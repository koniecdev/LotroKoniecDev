using Avalonia;
using Avalonia.Headless;
using LotroKoniecDev.DesktopApp.Presentation;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

/// <summary>
/// The real <see cref="App"/>, with its theme, on Avalonia's headless platform: no window opens, and it
/// runs on the Linux CI runner as well (ADR-0033).
/// </summary>
public static class HeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
