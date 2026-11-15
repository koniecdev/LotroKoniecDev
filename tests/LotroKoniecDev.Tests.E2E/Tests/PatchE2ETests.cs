using System.Security.Cryptography;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.E2E.Tests;

/// <summary>
/// Every test here uses one shared copy of the DAT, patched once with the repo's translation file
/// (<see cref="PatchedDatCopy"/>, #1408).
/// </summary>
[Collection("E2E")]
public sealed class PatchE2ETests : IClassFixture<PatchedDatCopy>
{
    private readonly E2ETestFixture _fixture;
    private readonly PatchedDatCopy _patchedDatCopy;

    public PatchE2ETests(E2ETestFixture fixture, PatchedDatCopy patchedDatCopy)
    {
        _fixture = fixture;
        _patchedDatCopy = patchedDatCopy;
    }

    [SkippableFact]
    public async Task Patch_ShouldExitWithZero_WhenRealDatWithPolishTxt()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Act
        PatchedDat patched = await _patchedDatCopy.GetAsync();

        //Assert
        patched.Patch.ExitCode.ShouldBe((int)CliExitCode.Success, $"stderr: {patched.Patch.Stderr}");
        patched.Patch.Stderr.ShouldBeNullOrWhiteSpace(
            "Successful operation should not produce stderr output");
    }

    [SkippableFact]
    public async Task Patch_ShouldModifyDatFile_WhenRealDatWithPolishTxt()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Act
        PatchedDat patched = await _patchedDatCopy.GetAsync();

        //Assert
        patched.Patch.ExitCode.ShouldBe((int)CliExitCode.Success, $"stderr: {patched.Patch.Stderr}");
        patched.LastWriteAfterPatchUtc.ShouldBeGreaterThan(patched.LastWriteBeforePatchUtc,
            "DAT file should be modified after patching");
    }

    [SkippableFact]
    public async Task Patch_ShouldCreateBackupFile_WhenRealDatFile()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Act
        PatchedDat patched = await _patchedDatCopy.GetAsync();

        //Assert
        patched.Patch.ExitCode.ShouldBe((int)CliExitCode.Success, $"stderr: {patched.Patch.Stderr}");
        File.Exists(patched.BackupPath).ShouldBeTrue(
            "Backup file should be created alongside the DAT file");
        new FileInfo(patched.BackupPath).Length.ShouldBe(patched.SizeBeforePatch,
            "Backup should have the same size as the original DAT");
    }

    [SkippableFact]
    public async Task Patch_ShouldReuseExistingBackup_WhenPatchedTwice()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange: the shared first patch created the backup
        PatchedDat patched = await _patchedDatCopy.GetAsync();
        patched.Patch.ExitCode.ShouldBe((int)CliExitCode.Success, $"First patch failed: {patched.Patch.Stderr}");

        DateTime backupModifiedAfterFirst = File.GetLastWriteTimeUtc(patched.BackupPath);

        //Act: second patch should reuse existing backup
        CliResult secondPatch = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" -d \"{patched.DatPath}\"");

        //Assert
        secondPatch.ExitCode.ShouldBe((int)CliExitCode.Success, $"Second patch failed: {secondPatch.Stderr}");
        File.GetLastWriteTimeUtc(patched.BackupPath).ShouldBe(backupModifiedAfterFirst,
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

        PatchedDat patched = await _patchedDatCopy.GetAsync();

        //Act: use short name "polish" (no path separators, no .txt extension)
        CliResult result = await _fixture.RunCliAsync(
            $"patch polish -d \"{patched.DatPath}\"",
            workingDirectory: workDir);

        //Assert: should resolve "polish" → "translations/polish.txt" and succeed
        result.ExitCode.ShouldBe((int)CliExitCode.Success,
            $"Short name 'polish' should resolve to translations/polish.txt. stdout: {result.Stdout}");
    }

    /// <summary>
    /// The read-write open path must still require write access, so patching a read-only copy of the
    /// DAT fails and changes nothing (#629).
    /// </summary>
    [SkippableFact]
    public async Task Patch_ShouldFailAndLeaveDatUnchanged_WhenDatCopyIsReadOnly()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange
        PatchedDat patched = await _patchedDatCopy.GetAsync();
        File.SetAttributes(patched.DatPath, File.GetAttributes(patched.DatPath) | FileAttributes.ReadOnly);
        byte[] hashBefore = await ComputeFileHashAsync(patched.DatPath);

        try
        {
            //Act
            CliResult result = await _fixture.RunCliAsync(
                $"patch \"{_fixture.TranslationsPolishPath}\" -d \"{patched.DatPath}\"");

            //Assert
            result.ExitCode.ShouldNotBe((int)CliExitCode.Success,
                "Patching a read-only DAT should fail because the read-write open demands write access");
            byte[] hashAfter = await ComputeFileHashAsync(patched.DatPath);
            hashAfter.ShouldBe(hashBefore, "A failed patch must not change a single byte of the DAT");
        }
        finally
        {
            // The other tests of this class patch the same copy again.
            File.SetAttributes(patched.DatPath, File.GetAttributes(patched.DatPath) & ~FileAttributes.ReadOnly);
        }
    }

    [SkippableFact]
    public async Task Roundtrip_ShouldProducePolishTexts_WhenExportPatchExport()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange: parse polish.txt to get target IDs and expected content
        TranslationFileParser parser = new();
        Result<TranslationParseResult> polishResult = parser.ParseFile(_fixture.TranslationsPolishPath);
        polishResult.IsSuccess.ShouldBeTrue(
            $"polish.txt should parse. Error: {(polishResult.IsFailure ? polishResult.Error.Message : "")}");
        polishResult.Value.Translations.ShouldNotBeEmpty("polish.txt should contain translations");

        //Arrange: use cached export as "before" (original English DAT)
        _fixture.CachedExportResult!.ExitCode.ShouldBe((int)CliExitCode.Success,
            $"Cached export failed: {_fixture.CachedExportResult.Stderr}");

        //Act: the shared copy is patched once and exported right after that patch
        PatchedDat patched = await _patchedDatCopy.GetAsync();
        patched.Patch.ExitCode.ShouldBe((int)CliExitCode.Success, $"Patch failed: {patched.Patch.Stderr}");
        patched.Patch.Stderr.ShouldBeNullOrWhiteSpace(
            "Patch should not produce stderr output");
        patched.ExportAfterPatch.ExitCode.ShouldBe((int)CliExitCode.Success,
            $"Export after failed: {patched.ExportAfterPatch.Stderr}");
        patched.ExportAfterPatch.Stderr.ShouldBeNullOrWhiteSpace(
            "Export after should not produce stderr output");

        //Assert: build O(1) lookup indexes for both exports
        Dictionary<string, string> beforeIndex = BuildLineIndex(_fixture.CachedExportPath);
        Dictionary<string, string> afterIndex = BuildLineIndex(patched.ExportAfterPatchPath);

        foreach (Translation expected in polishResult.Value.Translations)
        {
            string linePrefix = $"{expected.FileId}||{expected.GossipId}||";

            beforeIndex.TryGetValue(linePrefix, out string? beforeLine);
            afterIndex.TryGetValue(linePrefix, out string? afterLine);

            beforeLine.ShouldNotBeNull(
                $"Before export should contain FileId={expected.FileId}, GossipId={expected.GossipId}");
            afterLine.ShouldNotBeNull(
                $"After export should contain FileId={expected.FileId}, GossipId={expected.GossipId}");

            Result<Translation> afterParsed = parser.ParseLine(afterLine);
            afterParsed.IsSuccess.ShouldBeTrue(
                $"After line should parse for GossipId={expected.GossipId}");

            // After patch content should match the Polish translation exactly
            afterParsed.Value.Content.ShouldBe(expected.Content,
                $"Patched content should match polish.txt for GossipId={expected.GossipId}");
        }
    }

    [SkippableFact]
    public async Task Roundtrip_ShouldPreserveUntranslatedTexts_WhenPatchApplied()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange: get set of translated IDs to exclude
        TranslationFileParser parser = new();
        Result<TranslationParseResult> polishResult = parser.ParseFile(_fixture.TranslationsPolishPath);
        polishResult.IsSuccess.ShouldBeTrue(
            $"polish.txt should parse. Error: {(polishResult.IsFailure ? polishResult.Error.Message : "")}");

        HashSet<string> translatedKeys = polishResult.Value.Translations
            .Select(t => $"{t.FileId}||{t.GossipId}||")
            .ToHashSet();

        //Arrange: use cached export as "before"
        _fixture.CachedExportResult!.ExitCode.ShouldBe((int)CliExitCode.Success,
            $"Cached export failed: {_fixture.CachedExportResult.Stderr}");

        //Act: the shared copy is patched once and exported right after that patch
        PatchedDat patched = await _patchedDatCopy.GetAsync();
        patched.Patch.ExitCode.ShouldBe((int)CliExitCode.Success, $"Patch failed: {patched.Patch.Stderr}");
        patched.ExportAfterPatch.ExitCode.ShouldBe((int)CliExitCode.Success,
            $"Export after failed: {patched.ExportAfterPatch.Stderr}");

        //Assert: sample untranslated lines and verify they're identical
        Dictionary<string, string> beforeIndex = BuildLineIndex(_fixture.CachedExportPath);
        Dictionary<string, string> afterIndex = BuildLineIndex(patched.ExportAfterPatchPath);

        List<string> untranslatedKeys = beforeIndex.Keys
            .Where(k => !translatedKeys.Contains(k))
            .Take(500)
            .ToList();

        untranslatedKeys.Count.ShouldBeGreaterThan(0,
            "Should have untranslated lines to verify");

        foreach (string key in untranslatedKeys)
        {
            afterIndex.TryGetValue(key, out string? afterLine);
            afterLine.ShouldNotBeNull($"Untranslated line {key} should still exist after patch");
            afterLine.ShouldBe(beforeIndex[key],
                $"Untranslated line {key} should be identical after patch");
        }
    }

    private static async Task<byte[]> ComputeFileHashAsync(string filePath)
    {
        await using FileStream stream = File.OpenRead(filePath);
        return await SHA256.HashDataAsync(stream);
    }

    private static Dictionary<string, string> BuildLineIndex(string filePath)
    {
        Dictionary<string, string> index = new();
        foreach (string line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            int firstSep = line.IndexOf("||", StringComparison.Ordinal);
            if (firstSep < 0)
            {
                continue;
            }

            int secondSep = line.IndexOf("||", firstSep + 2, StringComparison.Ordinal);
            if (secondSep < 0)
            {
                continue;
            }

            string key = line[..(secondSep + 2)];
            index.TryAdd(key, line);
        }

        return index;
    }
}
