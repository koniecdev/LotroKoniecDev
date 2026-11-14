using System.Diagnostics;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Infrastructure.Diagnostics;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The file check behind the patch's refusal messages (#1248), against real files. Each way a file
/// can refuse a write must come back as its own kind, and the check must never change the file.
/// </summary>
public sealed class WriteAccessCheckerTests : IDisposable
{
    private static readonly byte[] FileContent = [0x4C, 0x4F, 0x54, 0x52, 0x4F];
    private static readonly DateTime FixedLastWriteTimeUtc = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"lotro-write-access-tests-{Guid.NewGuid():N}");

    private readonly WriteAccessChecker _sut = new();

    private string DatFilePath => Path.Combine(_directory, "client_local_English.dat");

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        // Best effort: when the ACL test could not restore its file, a throw here would hide the
        // test's own result.
        try
        {
            foreach (string file in Directory.GetFiles(_directory))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }

    [Fact]
    public void CheckFile_WritableFile_ShouldReportNoRefusal()
    {
        // Arrange
        CreateDatFile();

        // Act
        DatFileWriteCheck check = _sut.CheckFile(DatFilePath);

        // Assert
        check.Refusal.ShouldBe(DatFileWriteRefusal.None);
    }

    [Fact]
    public void CheckFile_WritableFile_ShouldLeaveItsBytesAndLastWriteTimeUnchanged()
    {
        // Arrange: the check runs on the player's live DAT.
        CreateDatFile();

        // Act
        _sut.CheckFile(DatFilePath);

        // Assert
        File.ReadAllBytes(DatFilePath).ShouldBe(FileContent);
        File.GetLastWriteTimeUtc(DatFilePath).ShouldBe(FixedLastWriteTimeUtc);
    }

    [Fact]
    public void CheckFile_ReadOnlyFile_ShouldReportReadOnly()
    {
        // Arrange
        CreateDatFile();
        File.SetAttributes(DatFilePath, File.GetAttributes(DatFilePath) | FileAttributes.ReadOnly);

        // Act
        DatFileWriteCheck check = _sut.CheckFile(DatFilePath);

        // Assert
        check.Refusal.ShouldBe(DatFileWriteRefusal.ReadOnly);
    }

    /// <summary>
    /// The game folder's own ACL gives Users read and execute rights only. That refusal is not the
    /// Read-only flag, so it needs its own advice.
    /// </summary>
    [Fact]
    public async Task CheckFile_FileWhoseAclGrantsReadButNotWrite_ShouldReportAccessDenied()
    {
        // Arrange
        CreateDatFile();
        string account = $"{Environment.UserDomainName}\\{Environment.UserName}";

        // Never write this as /deny "(W)": FILE_GENERIC_WRITE includes SYNCHRONIZE, so denying it
        // blocks reads too (#629). The precondition checks below catch that trap.
        int aclExitCode = await RunIcaclsAsync($"\"{DatFilePath}\" /inheritance:r /grant:r \"{account}:(RX)\"");
        aclExitCode.ShouldBe(0, "the test could not set up its own ACL");

        try
        {
            CanOpen(DatFilePath, FileAccess.Read).ShouldBeTrue(
                "precondition: the ACL must still allow reads, or this proves nothing");
            CanOpen(DatFilePath, FileAccess.Write).ShouldBeFalse(
                "precondition: the ACL must deny writes, or this proves nothing");

            // Act
            DatFileWriteCheck check = _sut.CheckFile(DatFilePath);

            // Assert
            check.Refusal.ShouldBe(DatFileWriteRefusal.AccessDenied);
        }
        finally
        {
            // Best effort, so Dispose can delete the file. Never assert here: a failure would hide
            // what the test was reporting.
            _ = await RunIcaclsAsync($"\"{DatFilePath}\" /inheritance:e /grant \"{account}:(F)\"");
        }
    }

    [Fact]
    public void CheckFile_FileAnotherHandleHoldsWithoutSharing_ShouldReportInUse()
    {
        // Arrange
        CreateDatFile();
        using FileStream holder = File.Open(DatFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        DatFileWriteCheck check = _sut.CheckFile(DatFilePath);

        // Assert
        check.Refusal.ShouldBe(DatFileWriteRefusal.InUse);
        check.OsReason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CheckFile_MissingFile_ShouldReportNotFound()
    {
        // Arrange: the file was deleted after the CLI checked that it exists.
        Directory.CreateDirectory(_directory);

        // Act
        DatFileWriteCheck check = _sut.CheckFile(DatFilePath);

        // Assert
        check.Refusal.ShouldBe(DatFileWriteRefusal.NotFound);
    }

    [Fact]
    public void CheckFile_MissingFolder_ShouldReportNotFound()
    {
        // Act
        DatFileWriteCheck check = _sut.CheckFile(DatFilePath);

        // Assert
        check.Refusal.ShouldBe(DatFileWriteRefusal.NotFound);
    }

    [Fact]
    public void CheckFile_PathTheSystemRejects_ShouldReportOtherWithTheSystemReasonInsteadOfThrowing()
    {
        // Arrange: a NUL character, which every path API rejects before it reaches the disk.
        string badPath = Path.Combine(_directory, "client\0local.dat");

        // Act
        DatFileWriteCheck check = _sut.CheckFile(badPath);

        // Assert
        check.Refusal.ShouldBe(DatFileWriteRefusal.Other);
        check.OsReason.ShouldNotBeNullOrWhiteSpace();
    }

    private static bool CanOpen(string path, FileAccess access)
    {
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, access, FileShare.ReadWrite);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<int> RunIcaclsAsync(string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "icacls",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        // Read both pipes at once: reading one to its end first can block when the other fills up.
        await Task.WhenAll(process.StandardError.ReadToEndAsync(), process.StandardOutput.ReadToEndAsync());
        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    private void CreateDatFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(DatFilePath, FileContent);
        File.SetLastWriteTimeUtc(DatFilePath, FixedLastWriteTimeUtc);
    }
}
