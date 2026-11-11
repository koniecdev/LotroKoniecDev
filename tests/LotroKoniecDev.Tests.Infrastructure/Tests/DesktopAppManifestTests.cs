using System.Text;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The manifest Windows reads from <c>LotroPoPolsku.exe</c>. The project ships its own manifest, so the
/// <c>asInvoker</c> entry the CLI gets from the compiler's default one is there only if the file says so.
/// </summary>
public sealed class DesktopAppManifestTests
{
    [Fact]
    public void LotroPoPolskuExe_EmbeddedManifest_RunsAsInvoker()
    {
        // Arrange: the referenced exe is copied next to this suite's own assembly.
        string exePath = Path.Combine(AppContext.BaseDirectory, "LotroPoPolsku.exe");

        // Act
        string exeContent = Encoding.Latin1.GetString(File.ReadAllBytes(exePath));

        // Assert: a plain bool, so a failure does not print the whole binary.
        exeContent.Contains("<requestedExecutionLevel level=\"asInvoker\"", StringComparison.Ordinal).ShouldBeTrue(
            "Without it, Windows treats the 32-bit app as a legacy one and quietly redirects its writes under " +
            "Program Files to the VirtualStore folder, so a patch never reaches the real DAT.");
    }
}
