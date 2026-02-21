namespace LotroKoniecDev.Tests.E2E.Tests;

[Collection("E2E")]
public sealed class ErrorPathE2ETests
{
    private readonly E2ETestFixture _fixture;

    public ErrorPathE2ETests(E2ETestFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Export_ShouldReturnFileNotFound_WhenDatPathDoesNotExist()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        string fakeDatPath = Path.Combine(_fixture.CreateTempDir(), "nonexistent.dat");
        string outputPath = Path.Combine(_fixture.CreateTempDir(), "export.txt");

        CliResult result = await _fixture.RunCliAsync(
            $"export \"{fakeDatPath}\" \"{outputPath}\"");

        result.ExitCode.ShouldBe(2, $"stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Patch_ShouldReturnFileNotFound_WhenTranslationFileDoesNotExist()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        string tempDatPath = _fixture.CreateTempDatCopy();
        string fakeTranslationsPath = Path.Combine(_fixture.CreateTempDir(), "nonexistent.txt");

        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{fakeTranslationsPath}\" \"{tempDatPath}\"");

        result.ExitCode.ShouldBe(2, $"stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Patch_ShouldReturnFileNotFound_WhenDatPathDoesNotExist()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        string fakeDatPath = Path.Combine(_fixture.CreateTempDir(), "nonexistent.dat");

        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{fakeDatPath}\"");

        result.ExitCode.ShouldBe(2, $"stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Patch_ShouldReturnInvalidArgs_WhenNoTranslationNameProvided()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        CliResult result = await _fixture.RunCliAsync("patch");

        result.ExitCode.ShouldBe(1, $"stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Cli_ShouldReturnInvalidArgs_WhenUnknownCommand()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        CliResult result = await _fixture.RunCliAsync("gibberish");

        result.ExitCode.ShouldBe(1, $"stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Export_ShouldFail_WhenOutputDirDoesNotExist()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        string badOutputPath = Path.Combine(_fixture.CreateTempDir(), "nonexistent_subdir", "export.txt");

        CliResult result = await _fixture.RunCliAsync(
            $"export \"{_fixture.DatFilePath}\" \"{badOutputPath}\"");

        result.ExitCode.ShouldBe(3, $"Export to non-existent directory should fail. stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Patch_ShouldFail_WhenTranslationFileHasOnlyGarbage()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange — create a translation file with only unparseable lines
        string garbagePath = Path.Combine(_fixture.CreateTempDir(), "garbage.txt");
        await File.WriteAllTextAsync(garbagePath, """
            not a valid line at all
            also garbage
            missing||fields
            """);
        string tempDatPath = _fixture.CreateTempDatCopy();

        //Act
        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{garbagePath}\" \"{tempDatPath}\"");

        //Assert — parser returns empty list → Patcher returns NoTranslations → exit code 3
        result.ExitCode.ShouldBe(3, $"Patch with garbage translations should fail. stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Patch_ShouldFail_WhenTranslationFileHasOnlyComments()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange — create a translation file with only comments and blanks
        string commentsPath = Path.Combine(_fixture.CreateTempDir(), "comments_only.txt");
        await File.WriteAllTextAsync(commentsPath, """
            # This is a comment
            # Another comment

            # Yet another
            """);
        string tempDatPath = _fixture.CreateTempDatCopy();

        //Act
        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{commentsPath}\" \"{tempDatPath}\"");

        //Assert — all lines skipped → empty translations → exit code 3
        result.ExitCode.ShouldBe(3, $"Patch with comment-only file should fail. stdout: {result.Stdout}");
    }
}
