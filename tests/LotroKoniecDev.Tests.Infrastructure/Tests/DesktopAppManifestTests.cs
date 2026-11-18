using System.Text;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The manifest Windows reads from <c>LotroPoPolsku.exe</c>. The project ships its own manifest, so the
/// level it asks for is there only if the file says so.
/// </summary>
public sealed class DesktopAppManifestTests
{
    [Fact]
    public void LotroPoPolskuExe_EmbeddedManifest_AsksForAdminRightsAtStart()
    {
        // Arrange: the referenced exe is copied next to this suite's own assembly.
        string exePath = Path.Combine(AppContext.BaseDirectory, "LotroPoPolsku.exe");

        // Act
        string exeContent = Encoding.Latin1.GetString(File.ReadAllBytes(exePath));

        // Assert: a plain bool, so a failure does not print the whole binary.
        exeContent.Contains("<requestedExecutionLevel level=\"requireAdministrator\"", StringComparison.Ordinal).ShouldBeTrue(
            "The app asks for admin rights once, at start (owner decision 2 on #42, #1464): a patch writes the " +
            "game's DAT under Program Files, and the app's own files are safe only when every copy runs elevated.");
    }
}
