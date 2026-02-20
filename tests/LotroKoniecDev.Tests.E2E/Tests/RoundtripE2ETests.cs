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
    public async Task Roundtrip_ExportPatchExport_PatchedTextsArePolish()
    {
        Skip.If(!_fixture.IsDatFileAvailable, "DAT file not found in TestData/");

        //Arrange — parse polish.txt to get target IDs and expected content
        TranslationFileParser parser = new();
        Result<IReadOnlyList<Translation>> polishResult = parser.ParseFile(_fixture.TranslationsPolishPath);
        polishResult.IsSuccess.ShouldBeTrue(
            $"polish.txt should parse. Error: {(polishResult.IsFailure ? polishResult.Error.Message : "")}");
        polishResult.Value.ShouldNotBeEmpty("polish.txt should contain translations");

        //Arrange — export original DAT (before patch)
        string beforePath = Path.Combine(_fixture.CreateTempDir(), "before.txt");
        CliResult exportBefore = await _fixture.RunCliAsync(
            $"export \"{_fixture.DatFilePath}\" \"{beforePath}\"");
        exportBefore.ExitCode.ShouldBe(0, $"Export before failed: {exportBefore.Stderr}");

        //Arrange — copy DAT to temp and patch it
        string tempDatPath = _fixture.CreateTempDatCopy();
        CliResult patch = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" \"{tempDatPath}\"");
        patch.ExitCode.ShouldBe(0, $"Patch failed: {patch.Stderr}");

        //Act — export the patched DAT
        string afterPath = Path.Combine(_fixture.CreateTempDir(), "after.txt");
        CliResult exportAfter = await _fixture.RunCliAsync(
            $"export \"{tempDatPath}\" \"{afterPath}\"");
        exportAfter.ExitCode.ShouldBe(0, $"Export after failed: {exportAfter.Stderr}");

        //Assert — each Polish translation should now appear in the patched export
        foreach (Translation expected in polishResult.Value)
        {
            string linePrefix = $"{expected.FileId}||{expected.GossipId}||";

            string? beforeLine = FindLine(beforePath, linePrefix);
            string? afterLine = FindLine(afterPath, linePrefix);

            beforeLine.ShouldNotBeNull(
                $"Before export should contain FileId={expected.FileId}, GossipId={expected.GossipId}");
            afterLine.ShouldNotBeNull(
                $"After export should contain FileId={expected.FileId}, GossipId={expected.GossipId}");

            Result<Translation> beforeParsed = parser.ParseLine(beforeLine);
            Result<Translation> afterParsed = parser.ParseLine(afterLine);

            beforeParsed.IsSuccess.ShouldBeTrue(
                $"Before line should parse for GossipId={expected.GossipId}");
            afterParsed.IsSuccess.ShouldBeTrue(
                $"After line should parse for GossipId={expected.GossipId}");

            Translation before = beforeParsed.Value;
            Translation after = afterParsed.Value;

            // Before patch = English, after patch = Polish — content must differ
            before.Content.ShouldNotBe(after.Content,
                $"Content should change after patch for GossipId={expected.GossipId}");

            // After patch content should match the Polish translation exactly
            after.Content.ShouldBe(expected.Content,
                $"Patched content should match polish.txt for GossipId={expected.GossipId}");
        }
    }

    private static string? FindLine(string filePath, string prefix) =>
        File.ReadLines(filePath)
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
}
