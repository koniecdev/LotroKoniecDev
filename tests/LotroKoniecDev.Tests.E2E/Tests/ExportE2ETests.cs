using System.Diagnostics;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Tests.E2E.Tests;

[Collection("E2E")]
public sealed class ExportE2ETests
{
    private readonly E2ETestFixture _fixture;

    public ExportE2ETests(E2ETestFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public void Export_ShouldExitWithZero_WhenRealDatFile()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        _fixture.CachedExportResult!.ExitCode.ShouldBe((int)CliExitCode.Success,
            $"stderr: {_fixture.CachedExportResult.Stderr}");
        _fixture.CachedExportResult.Stderr.ShouldBeNullOrWhiteSpace(
            "Successful operation should not produce stderr output");
    }

    [SkippableFact]
    public void Export_ShouldProduceNonEmptyFile_WhenRealDatFile()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        File.Exists(_fixture.CachedExportPath).ShouldBeTrue("Export file should exist");
        long fileSize = new FileInfo(_fixture.CachedExportPath).Length;
        fileSize.ShouldBeGreaterThan(0, "Export file should not be empty");
    }

    [SkippableFact]
    public void Export_ShouldNotModifyOriginalDat_WhenRealDatFile()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        new FileInfo(_fixture.DatFilePath).Length.ShouldBeGreaterThan(0,
            "Original DAT file should still exist and be non-empty after export");
    }

    [SkippableFact]
    public async Task Export_ShouldMatchExpectedFormat_WhenRealDatFile()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        string[] allLines = await File.ReadAllLinesAsync(_fixture.CachedExportPath);
        string[] dataLines = allLines
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
            .ToArray();

        dataLines.ShouldNotBeEmpty("Export should contain data lines");

        foreach (string line in dataLines)
        {
            string[] parts = line.Split("||");
            parts.Length.ShouldBeGreaterThanOrEqualTo(5,
                $"Line should have at least 5 ||-delimited fields: {line[..Math.Min(line.Length, 80)]}");

            int.TryParse(parts[0], out int fileId).ShouldBeTrue(
                $"FileId should be numeric: {parts[0]}");
            int highByte = fileId >> 24;
            highByte.ShouldBe(DatFileConstants.TextFileMarker,
                $"FileId high byte should be 0x25 (TextFileMarker): {line[..Math.Min(line.Length, 80)]}");
        }
    }

    [SkippableFact]
    public void Export_ShouldBeFullyParseable_WhenRealDatFile()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        TranslationFileParser parser = new();
        Result<TranslationParseResult> result = parser.ParseFile(_fixture.CachedExportPath);

        result.IsSuccess.ShouldBeTrue(
            $"Parser should succeed. Error: {(result.IsFailure ? result.Error.Message : "")}");
        result.Value.Translations.ShouldNotBeEmpty("Parsed translations should not be empty");
        result.Value.Warnings.ShouldBeEmpty("A real export must not contain a line the parser rejects");
    }

    [SkippableFact]
    public void Export_ShouldProduceThousandsOfFragments_WhenRealDatFile()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        int lineCount = File.ReadLines(_fixture.CachedExportPath)
            .Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'));

        const int minimalExpectedLines = 750000;
        lineCount.ShouldBeGreaterThan(
            minimalExpectedLines,
            $"Real DAT export should produce way more than {minimalExpectedLines} of text fragments");
    }

    /// <summary>
    /// Option A (#443) feasibility gate on real Windows: the read-only open path must succeed
    /// without write access to the file. The read-only attribute is the cheaper of the two ways
    /// Windows refuses a write — <see cref="Export_ShouldExitWithZero_WhenDatCopyGrantsReadButNotWrite"/>
    /// pins the ACL, which is what the live Program Files DAT actually carries.
    /// </summary>
    [SkippableFact]
    public async Task Export_ShouldExitWithZero_WhenDatCopyIsReadOnly()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange
        string tempDatPath = _fixture.CreateTempDatCopy();
        string outputPath = Path.Combine(Path.GetDirectoryName(tempDatPath)!, "readonly_export.txt");
        File.SetAttributes(tempDatPath, File.GetAttributes(tempDatPath) | FileAttributes.ReadOnly);

        try
        {
            //Act
            CliResult result = await _fixture.RunCliAsync(
                $"export -d \"{tempDatPath}\" -o \"{outputPath}\"");

            //Assert
            result.ExitCode.ShouldBe((int)CliExitCode.Success, $"stderr: {result.Stderr}");
            File.Exists(outputPath).ShouldBeTrue("Export file should exist");
            int dataLineCount = File.ReadLines(outputPath)
                .Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'));
            dataLineCount.ShouldBeGreaterThan(0,
                "Read-only export should produce data lines beyond the header");
        }
        finally
        {
            File.SetAttributes(tempDatPath, File.GetAttributes(tempDatPath) & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// The live Program Files DAT is not marked read-only — it carries an ACL granting Users
    /// Read &amp; Execute and nothing else. That is a different refusal from the read-only
    /// attribute, and it is the one #629 was wrong about, so it is pinned on its own.
    /// </summary>
    [SkippableFact]
    public async Task Export_ShouldExitWithZero_WhenDatCopyGrantsReadButNotWrite()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");
        Skip.IfNot(OperatingSystem.IsWindows(), "ACLs are a Windows concept");

        //Arrange
        string tempDatPath = _fixture.CreateTempDatCopy();
        string outputPath = Path.Combine(Path.GetDirectoryName(tempDatPath)!, "acl_export.txt");
        string account = $"{Environment.UserDomainName}\\{Environment.UserName}";

        // Grant Read & Execute and drop inheritance, mirroring the game directory's Users:(RX).
        // Never express this as /deny "(W)": FILE_GENERIC_WRITE carries SYNCHRONIZE, so denying it
        // blocks reads too and the copy stops being a Program Files proxy at all — the measurement
        // trap that produced #629's false conclusion. The preconditions below are what catch it.
        int aclExitCode = await RunIcaclsAsync($"\"{tempDatPath}\" /inheritance:r /grant:r \"{account}:(RX)\"");
        aclExitCode.ShouldBe(0, "the test could not set up its own ACL");

        try
        {
            CanOpen(tempDatPath, FileAccess.Read).ShouldBeTrue(
                "precondition: the ACL must still allow reads, or this proves nothing");
            CanOpen(tempDatPath, FileAccess.Write).ShouldBeFalse(
                "precondition: the ACL must deny writes, or this proves nothing");

            //Act
            CliResult result = await _fixture.RunCliAsync(
                $"export -d \"{tempDatPath}\" -o \"{outputPath}\"");

            //Assert
            result.ExitCode.ShouldBe((int)CliExitCode.Success, $"stderr: {result.Stderr}");
            File.Exists(outputPath).ShouldBeTrue("Export file should exist");
            int dataLineCount = File.ReadLines(outputPath)
                .Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'));
            dataLineCount.ShouldBeGreaterThan(0,
                "Export off a write-denied DAT should produce data lines beyond the header");
        }
        finally
        {
            // Best-effort restore so the fixture's temp sweep is not fighting our own ACL. Never
            // assert here: a failure would replace whatever the test was actually reporting.
            _ = await RunIcaclsAsync($"\"{tempDatPath}\" /inheritance:e /grant \"{account}:(F)\"");
        }
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
        await process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode;
    }
}
