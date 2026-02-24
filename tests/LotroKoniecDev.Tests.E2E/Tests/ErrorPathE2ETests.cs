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

        result.ExitCode.ShouldBe((int)CliExitCode.FileNotFound, $"stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Patch_ShouldReturnFileNotFound_WhenTranslationFileDoesNotExist()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        string tempDatPath = _fixture.CreateTempDatCopy();
        string fakeTranslationsPath = Path.Combine(_fixture.CreateTempDir(), "nonexistent.txt");

        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{fakeTranslationsPath}\" \"{tempDatPath}\"");

        result.ExitCode.ShouldBe((int)CliExitCode.FileNotFound, $"stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Patch_ShouldReturnFileNotFound_WhenDatPathDoesNotExist()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        string fakeDatPath = Path.Combine(_fixture.CreateTempDir(), "nonexistent.dat");

        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{fakeDatPath}\"");

        result.ExitCode.ShouldBe((int)CliExitCode.FileNotFound, $"stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Patch_ShouldReturnInvalidArgs_WhenNoTranslationNameProvided()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        CliResult result = await _fixture.RunCliAsync("patch");

        result.ExitCode.ShouldBe((int)CliExitCode.InvalidArguments, $"stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Cli_ShouldReturnInvalidArgs_WhenUnknownCommand()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        CliResult result = await _fixture.RunCliAsync("gibberish");

        result.ExitCode.ShouldBe((int)CliExitCode.InvalidArguments, $"stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Export_ShouldFail_WhenOutputDirDoesNotExist()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        string badOutputPath = Path.Combine(_fixture.CreateTempDir(), "nonexistent_subdir", "export.txt");

        CliResult result = await _fixture.RunCliAsync(
            $"export \"{_fixture.DatFilePath}\" \"{badOutputPath}\"");

        result.ExitCode.ShouldBe((int)CliExitCode.OperationFailed,
            $"Export to non-existent directory should fail. stdout: {result.Stdout}");
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
        result.ExitCode.ShouldBe((int)CliExitCode.OperationFailed,
            $"Patch with garbage translations should fail. stdout: {result.Stdout}");
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
        result.ExitCode.ShouldBe((int)CliExitCode.OperationFailed,
            $"Patch with comment-only file should fail. stdout: {result.Stdout}");
    }

    [SkippableFact]
    public async Task Patch_ShouldNotSendEmptyDatPath_WhenNoDatPathArgProvided()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Act — full translations path, but NO dat path argument
        CliResult result = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\"");

        //Assert — Bug sends empty string to handler → "not found: " with empty path.
        // Fixed code calls DatPathResolver → either finds DAT or returns meaningful error.
        result.Stdout.Contains("not found: \r\n").ShouldBeFalse(
            "Empty path in error indicates DatPathResolver was bypassed");
        result.Stdout.Contains("not found: \n").ShouldBeFalse(
            "Empty path in error indicates DatPathResolver was bypassed");
    }

    [SkippableFact]
    public async Task Export_ShouldNotSendEmptyDatPath_WhenNoExplicitPathProvided()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange — empty working dir: no data/ folder, no LOTRO standard paths
        string emptyWorkDir = _fixture.CreateTempDir();

        //Act — export with no args (no dat path, output defaults to data/exported.txt)
        CliResult result = await _fixture.RunCliAsync("export", workingDirectory: emptyWorkDir);

        //Assert — Bug sends empty string → "not found: " with empty path.
        // Fixed code checks for null and returns FileNotFound before reaching handler.
        result.Stdout.Contains("not found: \r\n").ShouldBeFalse(
            "Empty path in error indicates null DatPath guard was bypassed");
        result.Stdout.Contains("not found: \n").ShouldBeFalse(
            "Empty path in error indicates null DatPath guard was bypassed");
    }
}
