using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Tests.E2E;

/// <summary>
/// Contains end-to-end tests related to the export functionality for processing DAT files.
/// This class tests the behavior of the export command-line interface (CLI) to ensure proper
/// handling and generation of output files during the export process.
/// </summary>
[Collection("E2E")]
public sealed class ExportE2ETests
{
    private readonly E2ETestFixture _fixture;

    public ExportE2ETests(E2ETestFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Executes an end-to-end test for exporting a DAT file using the export command-line interface (CLI).
    /// Validates that the process completes successfully with an exit code of zero.
    /// </summary>
    /// <returns>
    /// A task representing the asynchronous test. Verifies that the export operation succeeds
    /// and outputs without errors, ensuring a zero exit code.
    /// </returns>
    [SkippableFact]
    public async Task Export_RealDatFile_ExitCodeZero()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");
        
        //Arrange
        string outputPath = Path.Combine(_fixture.CreateTempDir(), "export.txt");
        
        //Act
        CliResult result = await _fixture.RunCliAsync($"export \"{_fixture.DatFilePath}\" \"{outputPath}\"");

        //Assert
        result.ExitCode.ShouldBe(0, $"stderr: {result.Stderr}");
    }

    /// <summary>
    /// Executes an end-to-end test to verify that exporting a DAT file creates a valid output file.
    /// Ensures that the export command-line interface (CLI) produces a file with content,
    /// demonstrating the successful completion of the export operation.
    /// </summary>
    /// <returns>
    /// A task representing the asynchronous test. Confirms that the exported file exists in the
    /// expected location and contains data, indicating the file has been generated correctly.
    /// </returns>
    [SkippableFact]
    public async Task Export_RealDatFile_ProducesValidFile()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");
        
        //Arrange
        string outputPath = Path.Combine(_fixture.CreateTempDir(), "export.txt");

        //Act
        await _fixture.RunCliAsync($"export \"{_fixture.DatFilePath}\" \"{outputPath}\"");

        //Assert
        File.Exists(outputPath).ShouldBeTrue("Export file should exist");
        long fileSize = new FileInfo(outputPath).Length;
        fileSize.ShouldBeGreaterThan(0, "Export file should not be empty");
    }

    /// <summary>
    /// Executes a test for exporting a DAT file using the export command-line interface (CLI).
    /// Verifies that the generated output matches the expected format, ensuring proper parsing
    /// and data integrity within the output file.
    /// </summary>
    /// <returns>
    /// A task representing the asynchronous test. Confirms that the exported output contains
    /// valid data lines and meets format specifications, including proper delimiter usage
    /// and required field structure.
    /// </returns>
    [SkippableFact]
    public async Task Export_RealDatFile_OutputMatchesExpectedFormat()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");
        
        //Arrange
        string outputPath = Path.Combine(_fixture.CreateTempDir(), "export.txt");

        //Act
        await _fixture.RunCliAsync($"export \"{_fixture.DatFilePath}\" \"{outputPath}\"");

        //Assert
        string[] allLines = await File.ReadAllLinesAsync(outputPath);
        string[] actualTranslationsLines = allLines
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
            .ToArray();

        actualTranslationsLines.ShouldNotBeEmpty("Export should contain data lines");

        foreach (string line in actualTranslationsLines.Take(100))
        {
            string[] parts = line.Split("||");
            parts.Length.ShouldBeGreaterThanOrEqualTo(5,
                $"Line should have at least 5 ||-delimited fields: {line[..Math.Min(line.Length, 80)]}");

            int fileId = int.Parse(parts[0]);
            int highByte = fileId >> 24;
            highByte.ShouldBe(DatFileConstants.TextFileMarker,
                $"FileId high byte should be 0x25 (TextFileMarker): {line[..Math.Min(line.Length, 80)]}");
        }
    }

    /// <summary>
    /// Tests the export functionality of a DAT file by verifying that all lines
    /// in the generated output file can be successfully parsed using the
    /// <see cref="TranslationFileParser"/>.
    /// Validates that the parser produces a successful result and that the
    /// parsed translations are not empty.
    /// Skips execution if the required DAT file is not available.
    /// </summary>
    /// <returns>
    /// A task representing the asynchronous test. Ensures that the output of the
    /// export operation is fully parseable and contains valid translation entries.
    /// </returns>
    [SkippableFact]
    public async Task Export_RealDatFile_AllLinesParseableByTranslationFileParser()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");
        
        //Arrange
        string outputPath = Path.Combine(_fixture.CreateTempDir(), "export.txt");

        //Act
        await _fixture.RunCliAsync($"export \"{_fixture.DatFilePath}\" \"{outputPath}\"");
        
        //Assert
        TranslationFileParser parser = new();
        Result<IReadOnlyList<Translation>> result = parser.ParseFile(outputPath);

        result.IsSuccess.ShouldBeTrue(
            $"Parser should succeed. Error: {(result.IsFailure ? result.Error.Message : "")}");
        result.Value.ShouldNotBeEmpty("Parsed translations should not be empty");
    }

    /// <summary>
    /// Executes an end-to-end test for exporting a DAT file using the export command-line interface (CLI).
    /// Validates that the export operation generates an output file with a significantly large number of text fragments.
    /// </summary>
    /// <returns>
    /// A task representing the asynchronous test. Ensures that the output file has many non-empty,
    /// non-commented lines, verifying the expected scale of fragment generation.
    /// </returns>
    [SkippableFact]
    public async Task Export_RealDatFile_ProducesThousandsOfFragments()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");
        
        //Arrange
        string outputPath = Path.Combine(_fixture.CreateTempDir(), "export.txt");

        //Act
        await _fixture.RunCliAsync($"export \"{_fixture.DatFilePath}\" \"{outputPath}\"");

        //Assert
        int lineCount = File.ReadLines(outputPath)
            .Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'));

        const int expectedLines = 750000;
        lineCount.ShouldBeGreaterThan(expectedLines,
            $"Real DAT export should produce way more than {expectedLines} of text fragments");
    }
}
