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
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{tempDatPath}\"");

        //Assert
        result.ExitCode.ShouldBe(0, $"stderr: {result.Stderr}");
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
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{tempDatPath}\"");

        //Assert
        result.ExitCode.ShouldBe(0, $"stderr: {result.Stderr}");
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
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{tempDatPath}\"");

        //Assert
        result.ExitCode.ShouldBe(0, $"stderr: {result.Stderr}");
        File.Exists(expectedBackupPath).ShouldBeTrue(
            "Backup file should be created alongside the DAT file");
        new FileInfo(expectedBackupPath).Length.ShouldBe(originalSize,
            "Backup should have the same size as the original DAT");
    }

    [SkippableFact]
    public async Task Patch_ShouldReuseExistingBackup_WhenPatchedTwice()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange — first patch creates backup
        string tempDatPath = _fixture.CreateTempDatCopy();
        CliResult firstPatch = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{tempDatPath}\"");
        firstPatch.ExitCode.ShouldBe(0, $"First patch failed: {firstPatch.Stderr}");

        string backupPath = tempDatPath + ".backup";
        DateTime backupModifiedAfterFirst = File.GetLastWriteTimeUtc(backupPath);

        //Act — second patch should reuse existing backup
        CliResult secondPatch = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{tempDatPath}\"");

        //Assert
        secondPatch.ExitCode.ShouldBe(0, $"Second patch failed: {secondPatch.Stderr}");
        File.GetLastWriteTimeUtc(backupPath).ShouldBe(backupModifiedAfterFirst,
            "Backup should not be overwritten on second patch");
    }
}
