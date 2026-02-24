using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Tests.E2E.Tests;

[Collection("E2E")]
public sealed class RoundtripE2ETests
{
    private readonly E2ETestFixture _fixture;

    public RoundtripE2ETests(E2ETestFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Roundtrip_ShouldProducePolishTexts_WhenExportPatchExport()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange — parse polish.txt to get target IDs and expected content
        TranslationFileParser parser = new();
        Result<IReadOnlyList<Translation>> polishResult = parser.ParseFile(_fixture.TranslationsPolishPath);
        polishResult.IsSuccess.ShouldBeTrue(
            $"polish.txt should parse. Error: {(polishResult.IsFailure ? polishResult.Error.Message : "")}");
        polishResult.Value.ShouldNotBeEmpty("polish.txt should contain translations");

        //Arrange — use cached export as "before" (original English DAT)
        _fixture.CachedExportResult!.ExitCode.ShouldBe((int)CliExitCode.Success,
            $"Cached export failed: {_fixture.CachedExportResult.Stderr}");

        //Arrange — copy DAT to temp and patch it
        string tempDatPath = _fixture.CreateTempDatCopy();
        CliResult patch = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{tempDatPath}\"");
        patch.ExitCode.ShouldBe((int)CliExitCode.Success, $"Patch failed: {patch.Stderr}");
        patch.Stderr.ShouldBeNullOrWhiteSpace(
            "Patch should not produce stderr output");

        //Act — export the patched DAT
        string afterPath = Path.Combine(_fixture.CreateTempDir(), "after.txt");
        CliResult exportAfter = await _fixture.RunCliAsync(
            $"export \"{tempDatPath}\" \"{afterPath}\"");
        exportAfter.ExitCode.ShouldBe((int)CliExitCode.Success, $"Export after failed: {exportAfter.Stderr}");
        exportAfter.Stderr.ShouldBeNullOrWhiteSpace(
            "Export after should not produce stderr output");

        //Assert — build O(1) lookup indexes for both exports
        Dictionary<string, string> beforeIndex = BuildLineIndex(_fixture.CachedExportPath);
        Dictionary<string, string> afterIndex = BuildLineIndex(afterPath);

        foreach (Translation expected in polishResult.Value)
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

        //Arrange — get set of translated IDs to exclude
        TranslationFileParser parser = new();
        Result<IReadOnlyList<Translation>> polishResult = parser.ParseFile(_fixture.TranslationsPolishPath);
        polishResult.IsSuccess.ShouldBeTrue(
            $"polish.txt should parse. Error: {(polishResult.IsFailure ? polishResult.Error.Message : "")}");

        HashSet<string> translatedKeys = polishResult.Value
            .Select(t => $"{t.FileId}||{t.GossipId}||")
            .ToHashSet();

        //Arrange — use cached export as "before"
        _fixture.CachedExportResult!.ExitCode.ShouldBe((int)CliExitCode.Success,
            $"Cached export failed: {_fixture.CachedExportResult.Stderr}");

        //Arrange — patch a temp copy
        string tempDatPath = _fixture.CreateTempDatCopy();
        CliResult patch = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{tempDatPath}\"");
        patch.ExitCode.ShouldBe((int)CliExitCode.Success, $"Patch failed: {patch.Stderr}");

        //Act — export patched DAT
        string afterPath = Path.Combine(_fixture.CreateTempDir(), "after.txt");
        CliResult exportAfter = await _fixture.RunCliAsync(
            $"export \"{tempDatPath}\" \"{afterPath}\"");
        exportAfter.ExitCode.ShouldBe((int)CliExitCode.Success, $"Export after failed: {exportAfter.Stderr}");

        //Assert — sample untranslated lines and verify they're identical
        Dictionary<string, string> beforeIndex = BuildLineIndex(_fixture.CachedExportPath);
        Dictionary<string, string> afterIndex = BuildLineIndex(afterPath);

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
