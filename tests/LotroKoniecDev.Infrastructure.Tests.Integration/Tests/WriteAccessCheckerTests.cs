using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Infrastructure.Diagnostics;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Infrastructure.Tests.Integration.Tests;

/// <summary>
/// The file check behind the patch's refusal messages (#1248), against real files. Each way a file
/// can refuse a write must come back as its own kind, and the check must never change the file.
/// The ACL and sharing-violation cases need Windows, so they live in <c>Tests.Infrastructure</c>.
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

        // Best effort: a throw here would hide the test's own result.
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

    private void CreateDatFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(DatFilePath, FileContent);
        File.SetLastWriteTimeUtc(DatFilePath, FixedLastWriteTimeUtc);
    }
}
