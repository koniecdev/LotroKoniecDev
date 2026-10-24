using System.Text;
using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using PatcherCarvedLine = LotroKoniecDev.Application.Parsers.CarvedTranslationLine;
using PatcherCarver = LotroKoniecDev.Application.Parsers.TranslationLineCarver;
using PatcherEscaper = LotroKoniecDev.Application.Parsers.TranslationLineEscaper;
using TmsCarvedLine = LotroKoniecDev.TranslationSystem.API.Parsing.CarvedTranslationLine;
using TmsCarver = LotroKoniecDev.TranslationSystem.API.Parsing.TranslationLineCarver;
using TmsEscaper = LotroKoniecDev.TranslationSystem.API.Parsing.TranslationLineEscaper;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

/// <summary>
/// The drift guard of the <c>||</c> contract: the patcher's <see cref="TranslationFileParser"/> and the
/// TMS' <see cref="TranslationExportParser"/> are independent copies and must carve a line identically.
/// The args columns are the one difference in shape (the patcher keeps 0-indexed int arrays), so they
/// are rebuilt here for comparison.
/// </summary>
public sealed class ParserContractParityTests
{
    private const string AbsentArgs = "NULL";

    [Theory]
    [InlineData("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1")]
    [InlineData("620756992||1002||Tekst z <--DO_NOT_TOUCH!--> argumentem||1||1||1")]
    [InlineData("620756992||1003||Line with || inside the content||NULL||NULL||1")]
    [InlineData("620756992||1004||Multi||arg||content||1-2||1-2||1")]
    [InlineData("620756992||1005||Trailing approved zero||NULL||NULL||0")]
    [InlineData("100||200||leading||||NULL||NULL||1")]
    [InlineData("100||200||||trailing||NULL||NULL||1")]
    [InlineData("100||200||trailing pipe|||NULL||NULL||1")]
    [InlineData("100||200||three trailing pipes|||||1-2||3-4||1")]
    [InlineData("100||200|||||NULL||NULL||1")]
    [InlineData("100||200||trailing pipe|||||||1")]
    [InlineData("100||200||||||||1")]
    // Seven columns (ADR-0047): real digests, or the TMS parser would reject the row first.
    [InlineData("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||a37cc1683216cd32")]
    [InlineData("620756992||1002||Tekst z <--DO_NOT_TOUCH!--> argumentem||1||1||1||eacc6a53f9a2ae91")]
    [InlineData("620756992||1004||Multi||arg||content||1-2||1-2||1||2dcab7603ec22224")]
    [InlineData("620756992||1008||Koniec rury|||NULL||NULL||1||df0aa563e80483a5")]
    [InlineData("620756992||1009||Trzy rury|||||1-2||3-4||1||2f2d1cb2f502250a")]
    // Trailing spaces after a digest: still seven columns on both sides.
    [InlineData("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||a37cc1683216cd32 ")]
    [InlineData("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||a37cc1683216cd32\t")]
    public async Task BothParsers_OnTheSameContractLine_ShouldAgreeOnEveryField(string line)
    {
        // Arrange: the patcher parser is per-line; the TMS parser is per-stream.
        LotroKoniecDev.Domain.Models.Translation patcher = new TranslationFileParser().ParseLine(line).Value;

        ParsedExport tmsExport = await ParseAsync(line);
        ParsedExportRow tms = tmsExport.Rows.ShouldHaveSingleItem();

        // Assert
        patcher.FileId.ShouldBe(tms.FileId);
        ((long)patcher.GossipId).ShouldBe(tms.GossipId);
        patcher.Content.ShouldBe(tms.Content);
        patcher.IsApproved.ShouldBe(tms.Approved);
        patcher.SourceDigest.ShouldBe(tms.SourceDigest);
        ReconstructArgs(patcher.ArgsOrder).ShouldBe(NormalizeArgs(tms.ArgsOrder));
        ReconstructArgs(patcher.ArgsId).ShouldBe(NormalizeArgs(tms.ArgsId));
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public async Task BothParsers_OnNaughtyContent_ShouldCarveTheSameContractLineIdentically(string naughty)
    {
        // Arrange: injected raw, so this pins agreement, not round-trip fidelity; the per-parser suites
        // own fidelity (#569).
        string line = $"620756992||1001||{naughty}||1-2||1-2||1";

        LotroKoniecDev.Domain.Models.Translation patcher = new TranslationFileParser().ParseLine(line).Value;

        ParsedExport tmsExport = await ParseAsync(line);
        ParsedExportRow tms = tmsExport.Rows.ShouldHaveSingleItem();

        // Assert
        patcher.FileId.ShouldBe(tms.FileId);
        ((long)patcher.GossipId).ShouldBe(tms.GossipId);
        patcher.Content.ShouldBe(tms.Content);
        patcher.IsApproved.ShouldBe(tms.Approved);
        ReconstructArgs(patcher.ArgsOrder).ShouldBe(NormalizeArgs(tms.ArgsOrder));
        ReconstructArgs(patcher.ArgsId).ShouldBe(NormalizeArgs(tms.ArgsId));
    }

    [Theory]
    [InlineData(@"Line1\nLine2", "Line1\u000ALine2")]
    [InlineData(@"Line1\rLine2", "Line1\u000DLine2")]
    [InlineData(@"C:\\notes", @"C:\notes")]
    [InlineData(@"sekwencja \\n", @"sekwencja \n")]
    [InlineData(@"nieznana \t sekwencja", @"nieznana \t sekwencja")]
    public async Task BothParsers_OnAnEscapeSequence_ShouldUnfoldItIdentically(string escapedContent, string rawContent)
    {
        // Arrange: both readers answer with the raw text (ADR-0039). No naughty string carries such a
        // sequence, so these cases are composed by hand.
        string line = $"620756992||1001||{escapedContent}||NULL||NULL||1";

        // Act
        LotroKoniecDev.Domain.Models.Translation patcher = new TranslationFileParser().ParseLine(line).Value;
        ParsedExportRow tms = (await ParseAsync(line)).Rows.ShouldHaveSingleItem();

        // Assert
        patcher.Content.ShouldBe(rawContent);
        tms.Content.ShouldBe(rawContent);
    }

    [Fact]
    public async Task BothParsers_OnTheGoldenFixture_ShouldProduceTheSameContentForEveryRow()
    {
        // Arrange: the golden fixture is the contract, so it goes through both parsers. Comments and
        // blank lines are filtered the way the patcher's ParseFile does.
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "exported-sample.txt");
        string[] rowLines = (await File.ReadAllLinesAsync(path))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .ToArray();

        await using FileStream stream = File.OpenRead(path);
        ParsedExport tmsExport = await new TranslationExportParser().ParseAsync(stream, CancellationToken.None);

        // Act
        TranslationFileParser patcher = new();
        string[] patcherContents = rowLines.Select(line => patcher.ParseLine(line).Value.Content).ToArray();

        // Assert
        tmsExport.Errors.ShouldBeEmpty();
        tmsExport.Rows.Select(row => row.Content).ShouldBe(patcherContents);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void BothEscapers_OnTheSameInput_ShouldProduceIdenticalBytes(string naughty)
    {
        // The escape rule exists twice (ADR-0039). Each context's own suite stays green through a
        // one-sided change; only this test fails.
        PatcherEscaper.Escape(naughty).ShouldBe(TmsEscaper.Escape(naughty));
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.DelimiterHazards), MemberType = typeof(NaughtyStringCases))]
    public void BothCarvers_OnTheSameLine_ShouldFindIdenticalFieldBoundaries(string naughty)
    {
        // Arrange: the carver copies (ADR-0042), with a raw trailing pipe after the content (#597).
        string line = $"620756992||1001||{naughty}|||1-2||3-4||1";

        // Act
        PatcherCarver.TryCarve(line, out PatcherCarvedLine? patcher).ShouldBeTrue();
        TmsCarver.TryCarve(line, out TmsCarvedLine? tms).ShouldBeTrue();

        // Assert
        patcher!.FileId.ShouldBe(tms!.FileId);
        patcher.GossipId.ShouldBe(tms.GossipId);
        patcher.Content.ShouldBe(tms.Content);
        patcher.ArgsOrder.ShouldBe(tms.ArgsOrder);
        patcher.ArgsId.ShouldBe(tms.ArgsId);
        patcher.Approved.ShouldBe(tms.Approved);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.DelimiterHazards), MemberType = typeof(NaughtyStringCases))]
    public void BothCarvers_OnALineWithEmptyArgsColumns_ShouldFindIdenticalFieldBoundaries(string naughty)
    {
        // Arrange: empty args put the separator pairs side by side, so a backward search that straddles
        // its slice edge takes the wrong pair. With NULL args that mistake is invisible.
        string line = $"620756992||1001||{naughty}||||||1";

        // Act
        PatcherCarver.TryCarve(line, out PatcherCarvedLine? patcher).ShouldBeTrue();
        TmsCarver.TryCarve(line, out TmsCarvedLine? tms).ShouldBeTrue();

        // Assert
        patcher!.FileId.ShouldBe(tms!.FileId);
        patcher.GossipId.ShouldBe(tms.GossipId);
        patcher.Content.ShouldBe(tms.Content);
        patcher.ArgsOrder.ShouldBe(tms.ArgsOrder);
        patcher.ArgsId.ShouldBe(tms.ArgsId);
        patcher.Approved.ShouldBe(tms.Approved);
    }

    [Theory]
    [InlineData("620756992||1001||Content||1-x||NULL||1")]
    [InlineData("620756992||1001||Content||NULL||1-x||1")]
    [InlineData("620756992||1001||Content||1--2||NULL||1")]
    [InlineData("620756992||1001||Content||ONE||NULL||1")]
    public async Task BothParsers_OnAMalformedArgsColumn_ShouldRejectTheLine(string line)
    {
        // Malformed on both sides (ADR-0042), never swallowed by one and stored by the other (#597).
        new TranslationFileParser().ParseLine(line).IsFailure.ShouldBeTrue();

        ParsedExport tmsExport = await ParseAsync(line);
        tmsExport.Rows.ShouldBeEmpty();
        tmsExport.Errors.ShouldHaveSingleItem();
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public async Task PatcherExporterEscape_ThenTmsImportParse_ShouldRecoverTheOriginal(string naughty)
    {
        // Arrange: the production direction, patcher writer to TMS reader, which no other suite covers.
        string escaped = PatcherEscaper.Escape(naughty);
        string line = $"620756992||1001||{escaped}||NULL||NULL||1";

        // Act
        ParsedExport parsed = await ParseAsync(line);

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().Content.ShouldBe(naughty);
    }

    [Theory]
    [InlineData("Line1\nLine2")]
    [InlineData("Line1\r\nLine2")]
    [InlineData(@"C:\notes")]
    [InlineData(@"a||b\nc")]
    [InlineData("")]
    public async Task PatcherExporterEscape_ThenTmsImportParse_OnAHandComposedHazard_ShouldRecoverTheOriginal(string raw)
    {
        // Arrange: the naughty corpus carries no real newline, no escape sequence and no separator
        // combined with one, so the hazards this format is weakest at are composed by hand.
        string escaped = PatcherEscaper.Escape(raw);
        string line = $"620756992||1001||{escaped}||NULL||NULL||1";

        // Act
        ParsedExport parsed = await ParseAsync(line);

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().Content.ShouldBe(raw);
    }

    private static async Task<ParsedExport> ParseAsync(string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        return await new TranslationExportParser().ParseAsync(stream, CancellationToken.None);
    }

    private static string ReconstructArgs(int[]? args)
        => args is null ? AbsentArgs : string.Join('-', args.Select(value => value + 1));

    /// <summary>
    /// The patcher turns every "no arguments" form into a null array, while the TMS keeps the file form.
    /// Without this, an empty args column next to a trailing pipe, the boundary the backward pass must
    /// find (ADR-0042), could not be compared.
    /// </summary>
    private static string NormalizeArgs(string args)
        => string.IsNullOrWhiteSpace(args) || args.Equals(AbsentArgs, StringComparison.OrdinalIgnoreCase)
            ? AbsentArgs
            : args;

    [Fact]
    public async Task BothParsers_OnTheSevenColumnGoldenFixture_ShouldProduceTheSameContentAndDigestForEveryRow()
    {
        // Arrange: the seven-column golden fixture (ADR-0047), through both parsers.
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "exported-sample-digested.txt");
        string[] rowLines = (await File.ReadAllLinesAsync(path))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .ToArray();

        await using FileStream stream = File.OpenRead(path);
        ParsedExport tmsExport = await new TranslationExportParser().ParseAsync(stream, CancellationToken.None);

        // Act
        TranslationFileParser patcher = new();
        LotroKoniecDev.Domain.Models.Translation[] patcherRows =
            rowLines.Select(line => patcher.ParseLine(line).Value).ToArray();

        // Assert
        tmsExport.Errors.ShouldBeEmpty();
        tmsExport.Rows.Select(row => row.Content).ShouldBe(patcherRows.Select(row => row.Content));
        tmsExport.Rows.Select(row => row.SourceDigest).ShouldBe(patcherRows.Select(row => row.SourceDigest));
    }

    [Fact]
    public void BothDigestImplementations_OnTheSevenColumnGoldenFixturesFirstRow_ShouldAgree()
    {
        // SourceDigestGoldenCases pins both against an external value; this pins them against each
        // other on the fixture's own row, so a shared misreading cannot hide a divergence.
        LotroKoniecDev.Domain.Models.SourceDigest.Compute("Witaj w Srodziemiu!", null, null)
            .ShouldBe(SourceHash.Compute("Witaj w Srodziemiu!", null, null).ToWireDigest());
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.DelimiterHazards), MemberType = typeof(NaughtyStringCases))]
    public void BothCarvers_OnASevenColumnLine_ShouldFindIdenticalFieldBoundaries(string naughty)
    {
        // Arrange: the extra backward step of ADR-0047 must land in the same place in both copies.
        string line = $"620756992||1001||{naughty}|||1-2||3-4||1||a37cc1683216cd32";

        // Act
        PatcherCarver.TryCarve(line, out PatcherCarvedLine? patcher).ShouldBeTrue();
        TmsCarver.TryCarve(line, out TmsCarvedLine? tms).ShouldBeTrue();

        // Assert
        patcher!.Content.ShouldBe(tms!.Content);
        patcher.ArgsOrder.ShouldBe(tms.ArgsOrder);
        patcher.ArgsId.ShouldBe(tms.ArgsId);
        patcher.Approved.ShouldBe(tms.Approved);
        patcher.SourceDigest.ShouldBe(tms.SourceDigest);
    }
}
