using System.ComponentModel;
using System.Diagnostics;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.Windows.AppUpdating;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The held installer against the real filesystem (spec 0016). A real start uses the admin verb, so on
/// a machine without admin rights it shows the UAC prompt: those tests run only elevated, or with
/// <c>RUN_UAC_PROMPT_TESTS=true</c> and a person at the prompt, one filtered run per test. The other
/// start tests replace the start and need no prompt.
/// </summary>
public sealed class InstallerFileOpenerTests : IDisposable
{
    /// <summary>Independently computed (shell <c>printf 'installer bytes' | sha256sum</c>).</summary>
    private const string ContentHash = "e34210a6de4f653edf588301431c3d69a633638cbf587345cc50a7fed9f38f4c";

    private const string PromptTestsVariable = "RUN_UAC_PROMPT_TESTS";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lotro-installer-opener-tests-{Guid.NewGuid():N}");
    private readonly InstallerFileOpener _sut = new();

    private string InstallerPath => Path.Combine(_directory, "LotroPoPolsku-Setup-0.3.0.exe");

    private static bool PromptTestsOptedIn =>
        string.Equals(Environment.GetEnvironmentVariable(PromptTestsVariable), "true", StringComparison.Ordinal);

    /// <summary>
    /// A start test leaves a short-lived program running from this folder, and Windows refuses to
    /// delete a running exe, so the clean-up tries for a few seconds and then leaves the temp folder.
    /// </summary>
    public void Dispose()
    {
        for (int attempt = 0; attempt < 50 && Directory.Exists(_directory); attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(200));
            }
        }
    }

    [Fact]
    public void Open_AFile_ReturnsItsLowerCaseSha256()
    {
        // Arrange
        WriteInstaller();

        // Act
        Result<IOpenedInstaller> opened = _sut.Open(InstallerPath);

        // Assert
        using IOpenedInstaller installer = opened.Value;
        installer.Sha256Hex.ShouldBe(ContentHash);
    }

    [Fact]
    public void Open_WhileHeld_NoOtherProgramCanWriteDeleteOrRenameIt()
    {
        // Arrange
        WriteInstaller();
        using IOpenedInstaller installer = _sut.Open(InstallerPath).Value;

        // Act + Assert: until the installer has started, no other program can change what was hashed.
        Should.Throw<IOException>(() => File.WriteAllText(InstallerPath, "swapped"));
        Should.Throw<IOException>(() => File.Delete(InstallerPath));
        Should.Throw<IOException>(() => File.Move(InstallerPath, InstallerPath + ".old"));
    }

    [Fact]
    public void Open_WhileHeld_OtherReadersCanStillOpenIt()
    {
        // Arrange
        WriteInstaller();
        using IOpenedInstaller installer = _sut.Open(InstallerPath).Value;

        // Act: Windows opens the file for reading to start it, and Inno Setup reads it again the same way.
        string content = File.ReadAllText(InstallerPath);

        // Assert
        content.ShouldBe("installer bytes");
    }

    [Fact]
    public void Dispose_AHeldInstaller_ReleasesTheFile()
    {
        // Arrange
        WriteInstaller();
        IOpenedInstaller installer = _sut.Open(InstallerPath).Value;

        // Act
        installer.Dispose();

        // Assert
        File.Delete(InstallerPath);
        File.Exists(InstallerPath).ShouldBeFalse();
    }

    [Fact]
    public void CreateStartInfo_AnInstaller_StartsItWithAdminRightsThroughTheShell()
    {
        // Arrange
        const string arguments = "/SILENT \"/LOG=C:\\x y\\update-install.log\"";

        // Act
        ProcessStartInfo startInfo = InstallerFileOpener.CreateStartInfo(InstallerPath, arguments);

        // Assert
        startInfo.Verb.ShouldBe("runas");
        startInfo.UseShellExecute.ShouldBeTrue();
        startInfo.FileName.ShouldBe(InstallerPath);
        startInfo.Arguments.ShouldBe(arguments);
        startInfo.WorkingDirectory.ShouldBe(_directory);
    }

    [Fact]
    public void Start_AHeldInstaller_StartsTheHeldFileWithTheAdminVerbAndTheArguments()
    {
        // Arrange
        WriteInstaller();
        ProcessStartInfo? started = null;
        InstallerFileOpener sut = new(startInfo =>
        {
            started = startInfo;
            return Process.GetCurrentProcess();
        });
        using IOpenedInstaller installer = sut.Open(InstallerPath).Value;

        // Act
        Result start = installer.Start("/SILENT");

        // Assert
        start.IsSuccess.ShouldBeTrue();
        started.ShouldNotBeNull().Verb.ShouldBe("runas");
        started.FileName.ShouldBe(Path.GetFullPath(InstallerPath));
        started.Arguments.ShouldBe("/SILENT");
    }

    [Theory]
    [InlineData(1223, DomainErrors.AppUpdate.InstallerStartCancelledCode)]
    [InlineData(2, DomainErrors.AppUpdate.InstallerStartFailedCode)]
    [InlineData(193, DomainErrors.AppUpdate.InstallerStartFailedCode)]
    public void Start_WindowsRefusesTheStart_IsTheMatchingFailure(int win32Error, string expectedCode)
    {
        // Arrange: 1223 is the "No" at the admin prompt, every other refusal is a failed start.
        WriteInstaller();
        InstallerFileOpener sut = new(_ => throw new Win32Exception(win32Error));
        using IOpenedInstaller installer = sut.Open(InstallerPath).Value;

        // Act
        Result start = installer.Start("/SILENT");

        // Assert
        start.Error.Code.ShouldBe(expectedCode);
    }

    [Fact]
    public void Start_WindowsStartsNoProcess_IsAnInstallerStartFailure()
    {
        // Arrange
        WriteInstaller();
        InstallerFileOpener sut = new(_ => null);
        using IOpenedInstaller installer = sut.Open(InstallerPath).Value;

        // Act
        Result start = installer.Start("/SILENT");

        // Assert
        start.Error.Code.ShouldBe(DomainErrors.AppUpdate.InstallerStartFailedCode);
    }

    [SkippableFact]
    public void Start_WhileTheFileIsHeld_StartsIt()
    {
        // Click "Yes" at the prompt when it shows.
        SkipUnlessAPromptCanBeAnswered();

        // Arrange: a small real program in place of the installer. The design rests on Windows starting
        // a file the app still holds with read sharing only.
        Directory.CreateDirectory(_directory);
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), InstallerPath);
        using IOpenedInstaller installer = _sut.Open(InstallerPath).Value;

        // Act
        Result start = installer.Start(string.Empty);

        // Assert
        start.IsSuccess.ShouldBeTrue();
    }

    [SkippableFact]
    public void Start_TheAdminPromptRefused_IsInstallerStartCancelled()
    {
        // Click "No" at the prompt. An elevated run shows no prompt, so only an unelevated one can.
        Skip.If(Environment.IsPrivilegedProcess || !PromptTestsOptedIn,
            $"Needs a run without admin rights, with {PromptTestsVariable}=true and a person who clicks \"No\".");

        // Arrange
        Directory.CreateDirectory(_directory);
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), InstallerPath);
        using IOpenedInstaller installer = _sut.Open(InstallerPath).Value;

        // Act
        Result start = installer.Start(string.Empty);

        // Assert
        start.Error.Code.ShouldBe(DomainErrors.AppUpdate.InstallerStartCancelledCode);
    }

    [SkippableFact]
    public void Start_AFileWindowsCannotRun_IsAnInstallerStartFailure()
    {
        // Click "Yes" at the prompt if one shows.
        SkipUnlessAPromptCanBeAnswered();

        // Arrange
        WriteInstaller();
        using IOpenedInstaller installer = _sut.Open(InstallerPath).Value;

        // Act
        Result start = installer.Start(string.Empty);

        // Assert
        start.Error.Code.ShouldBe(DomainErrors.AppUpdate.InstallerStartFailedCode);
    }

    [Fact]
    public void Open_AMissingFile_IsAFileSystemError()
    {
        // Act
        Result<IOpenedInstaller> opened = _sut.Open(InstallerPath);

        // Assert
        opened.Error.Code.ShouldBe(DomainErrors.AppUpdate.FileSystemErrorCode);
    }

    private static void SkipUnlessAPromptCanBeAnswered() =>
        Skip.IfNot(Environment.IsPrivilegedProcess || PromptTestsOptedIn,
            $"Shows a UAC prompt: run elevated, or set {PromptTestsVariable}=true and answer it.");

    private void WriteInstaller()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(InstallerPath, "installer bytes");
    }
}
