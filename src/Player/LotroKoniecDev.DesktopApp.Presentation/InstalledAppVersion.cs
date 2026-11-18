namespace LotroKoniecDev.DesktopApp.Presentation;

/// <summary>
/// The version the exe was built with, as the release workflow stamped it (spec 0016). The exe reads
/// it from its own assembly and registers it; an unstamped build carries "0.0.0".
/// </summary>
public sealed record InstalledAppVersion(string Value);
