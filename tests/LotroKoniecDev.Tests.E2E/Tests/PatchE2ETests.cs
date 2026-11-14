using System.Security.Cryptography;

namespace LotroKoniecDev.Tests.E2E.Tests;

[Collection("E2E")]
public sealed class PatchE2ETests
{
    private readonly E2ETestFixture _fixture;

    public PatchE2ETests(E2ETestFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Patch_ShouldExitWithZero_WhenRealDatWithPolishTxt()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange
        string tempDatPath = _fixture.CreateTempDatCopy();

        //Act
        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" -d \"{tempDatPath}\"");

        //Assert
        result.ExitCode.ShouldBe((int)CliExitCode.Success, $"stderr: {result.Stderr}");
        result.Stderr.ShouldBeNullOrWhiteSpace(
            "Successful operation should not produce stderr output");
    }

    [SkippableFact]
    public async Task Patch_ShouldModifyDatFile_WhenRealDatWithPolishTxt()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange
        string tempDatPath = _fixture.CreateTempDatCopy();
        DateTime modifiedBefore = File.GetLastWriteTimeUtc(tempDatPath);

        //Act
        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" -d \"{tempDatPath}\"");

        //Assert
        result.ExitCode.ShouldBe((int)CliExitCode.Success, $"stderr: {result.Stderr}");
        DateTime modifiedAfter = File.GetLastWriteTimeUtc(tempDatPath);
        modifiedAfter.ShouldBeGreaterThan(modifiedBefore,
            "DAT file should be modified after patching");
    }

    [SkippableFact]
    public async Task Patch_ShouldCreateBackupFile_WhenRealDatFile()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange
        string tempDatPath = _fixture.CreateTempDatCopy();
        long originalSize = new FileInfo(tempDatPath).Length;
        string expectedBackupPath = tempDatPath + ".backup";

        //Act
        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" -d \"{tempDatPath}\"");

        //Assert
        result.ExitCode.ShouldBe((int)CliExitCode.Success, $"stderr: {result.Stderr}");
        File.Exists(expectedBackupPath).ShouldBeTrue(
            "Backup file should be created alongside the DAT file");
        new FileInfo(expectedBackupPath).Length.ShouldBe(originalSize,
            "Backup should have the same size as the original DAT");
    }

    [SkippableFact]
    public async Task Patch_ShouldReuseExistingBackup_WhenPatchedTwice()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange: first patch creates backup
        string tempDatPath = _fixture.CreateTempDatCopy();
        CliResult firstPatch = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" -d \"{tempDatPath}\"");
        firstPatch.ExitCode.ShouldBe((int)CliExitCode.Success, $"First patch failed: {firstPatch.Stderr}");

        string backupPath = tempDatPath + ".backup";
        DateTime backupModifiedAfterFirst = File.GetLastWriteTimeUtc(backupPath);

        //Act: second patch should reuse existing backup
        CliResult secondPatch = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" -d \"{tempDatPath}\"");

        //Assert
        secondPatch.ExitCode.ShouldBe((int)CliExitCode.Success, $"Second patch failed: {secondPatch.Stderr}");
        File.GetLastWriteTimeUtc(backupPath).ShouldBe(backupModifiedAfterFirst,
            "Backup should not be overwritten on second patch");
    }

    [SkippableFact]
    public async Task Patch_ShouldResolveShortTranslationName_WhenCalledWithBareName()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange: working dir with translations/polish.txt (mimics production layout)
        string workDir = _fixture.CreateTempDir();
        string translationsDir = Path.Combine(workDir, "translations");
        Directory.CreateDirectory(translationsDir);
        File.Copy(_fixture.TranslationsPolishPath, Path.Combine(translationsDir, "polish.txt"));

        string tempDatPath = _fixture.CreateTempDatCopy();

        //Act: use short name "polish" (no path separators, no .txt extension)
        CliResult result = await _fixture.RunCliAsync(
            $"patch polish -d \"{tempDatPath}\"",
            workingDirectory: workDir);

        //Assert: should resolve "polish" → "translations/polish.txt" and succeed
        result.ExitCode.ShouldBe((int)CliExitCode.Success,
            $"Short name 'polish' should resolve to translations/polish.txt. stdout: {result.Stdout}");
    }

    /// <summary>
    /// Patching a read-only copy of the DAT fails and changes nothing (#629). The preflight's file
    /// check refuses it before the backup and the read-write open (#1248).
    /// </summary>
    [SkippableFact]
    public async Task Patch_ShouldFailAndLeaveDatUnchanged_WhenDatCopyIsReadOnly()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange
        string tempDatPath = _fixture.CreateTempDatCopy();
        string tempDir = Path.GetDirectoryName(tempDatPath)!;
        File.SetAttributes(tempDatPath, File.GetAttributes(tempDatPath) | FileAttributes.ReadOnly);
        byte[] hashBefore = await ComputeFileHashAsync(tempDatPath);

        try
        {
            //Act
            CliResult result = await _fixture.RunCliAsync(
                $"patch \"{_fixture.TranslationsPolishPath}\" -d \"{tempDatPath}\"");

            //Assert
            result.ExitCode.ShouldNotBe((int)CliExitCode.Success,
                "Patching a read-only DAT should fail because the read-write open demands write access");
            byte[] hashAfter = await ComputeFileHashAsync(tempDatPath);
            hashAfter.ShouldBe(hashBefore, "A failed patch must not change a single byte of the DAT");
        }
        finally
        {
            // Clear the flag on every file, a .backup included if a regression made one (File.Copy
            // keeps the flag), or the fixture's cleanup of the temp directory fails on Windows.
            foreach (string file in Directory.GetFiles(tempDir))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
        }
    }

    /// <summary>
    /// A game file left read-only stops the patch before the backup, with a message that says how to
    /// clear the flag (#1248). A small stand-in is enough: nothing reads the DAT before the refusal.
    /// </summary>
    [SkippableFact]
    public async Task Patch_ShouldRefuseBeforeTheBackupAndSayHowToClearTheFlag_WhenDatIsReadOnly()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange
        string standInDatPath = Path.Combine(_fixture.CreateTempDir(), "client_local_English.dat");
        byte[] content = [0x4C, 0x4F, 0x54, 0x52, 0x4F];
        await File.WriteAllBytesAsync(standInDatPath, content);
        File.SetAttributes(standInDatPath, File.GetAttributes(standInDatPath) | FileAttributes.ReadOnly);

        try
        {
            //Act
            CliResult result = await _fixture.RunCliAsync(
                $"patch \"{_fixture.TranslationsPolishPath}\" -d \"{standInDatPath}\"");

            //Assert
            result.ExitCode.ShouldBe((int)CliExitCode.OperationFailed, $"stdout: {result.Stdout}");
            result.Stdout.ShouldContain("DatFile.ReadOnly");
            result.Stdout.ShouldContain(Path.GetFullPath(standInDatPath));
            result.Stdout.ShouldContain("clear the Read-only check box");
            File.Exists(standInDatPath + ".backup").ShouldBeFalse("the refusal must come before the backup");
            (await File.ReadAllBytesAsync(standInDatPath)).ShouldBe(content);
        }
        finally
        {
            // A .backup made by a regression would carry the flag too.
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(standInDatPath)!))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static async Task<byte[]> ComputeFileHashAsync(string filePath)
    {
        await using FileStream stream = File.OpenRead(filePath);
        return await SHA256.HashDataAsync(stream);
    }
}
