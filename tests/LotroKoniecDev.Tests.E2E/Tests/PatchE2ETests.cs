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
    public async Task Patch_RealDatWithPolishTxt_ExitCodeZero()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange
        string tempDatPath = _fixture.CreateTempDatCopy();

        //Act
        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{tempDatPath}\"");

        //Assert
        result.ExitCode.ShouldBe(0, $"stderr: {result.Stderr}");
    }

    [SkippableFact]
    public async Task Patch_RealDatWithPolishTxt_StdoutContainsApplied()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange
        string tempDatPath = _fixture.CreateTempDatCopy();

        //Act
        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{tempDatPath}\"");

        //Assert
        result.Stdout.ShouldContain("PATCH COMPLETE");
        result.Stdout.ShouldContain("Applied");
    }
}
