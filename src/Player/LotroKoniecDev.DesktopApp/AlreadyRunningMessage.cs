using System.Runtime.InteropServices;

namespace LotroKoniecDev.DesktopApp;

/// <summary>
/// The second copy's message, shown before Avalonia starts, so that copy touches nothing else. The
/// caption differs from the app's window title, so the release smoke test can tell this box from the
/// app (.github/workflows/desktop-release.yml).
/// </summary>
internal static partial class AlreadyRunningMessage
{
    internal const string Caption = "LOTRO po Polsku już działa";

    internal const string Text =
        "LOTRO po Polsku jest już uruchomione. Jego okno może być zminimalizowane albo otwarte na innym koncie Windows na tym komputerze.";

    private const uint IconInformation = 0x00000040;
    private const uint SetForeground = 0x00010000;

    public static void Show() => MessageBoxW(IntPtr.Zero, Text, Caption, IconInformation | SetForeground);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
