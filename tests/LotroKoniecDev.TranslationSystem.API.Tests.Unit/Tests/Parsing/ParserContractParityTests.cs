using System.Text;
using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using PatcherCarvedLine = LotroKoniecDev.Application.Parsers.CarvedTranslationLine;
using PatcherCarver = LotroKoniecDev.Application.Parsers.TranslationLineCarver;
using PatcherEscaper = LotroKoniecDev.Application.Parsers.TranslationLineEscaper;
using TmsCarvedLine = LotroKoniecDev.TranslationSystem.API.Parsing.CarvedTranslationLine;
using TmsCarver = LotroKoniecDev.TranslationSystem.API.Parsing.TranslationLineCarver;
using TmsEscaper = LotroKoniecDev.TranslationSystem.API.Parsing.TranslationLineEscaper;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

/// <summary>
/// The two bounded contexts own independent parsers of the same <c>||</c> contract
/// (CLAUDE.md: "share a data contract, not code"). This is the contract's drift guard: it pins that
/// the patcher's <see cref="TranslationFileParser"/> and the TMS' <see cref="TranslationExportParser"/>
/// carve an export line identically. Since ADR-0039 both unfold the content escape, so content is
/// compared directly on every input; the one remaining representational difference is the args
/// columns (the patcher converts them to 0-indexed int arrays), rebuilt here for comparison.
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
    public async Task BothParsers_OnTheSameContractLine_ShouldAgreeOnEveryField(string line)
    {
        // Arrange — the patcher parser is per-line; the TMS parser is per-stream.
        LotroKoniecDev.Domain.Models.Translation patcher = new TranslationFileParser().ParseLine(line).Value;

        ParsedExport tmsExport = await ParseAsync(line);
        ParsedExportRow tms = tmsExport.Rows.ShouldHaveSingleItem();

        // Assert — the args boundary is checked by rebuilding the verbatim args string from the
        // patcher's int arrays; everything else compares directly.
        patcher.FileId.ShouldBe(tms.FileId);
        ((long)patcher.GossipId).ShouldBe(tms.GossipId);
        patcher.Content.ShouldBe(tms.Content);
        patcher.IsApproved.ShouldBe(tms.Approved);
        ReconstructArgs(patcher.ArgsOrder).ShouldBe(NormalizeArgs(tms.ArgsOrder));
        ReconstructArgs(patcher.ArgsId).ShouldBe(NormalizeArgs(tms.ArgsId));
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public async Task BothParsers_OnNaughtyContent_ShouldCarveTheSameContractLineIdentically(string naughty)
    {
        // Arrange — hostile content is where two hand-written parsers of one format drift apart
        // first (#569). The corpus is injected RAW, so this asserts the two parsers agree on
        // whatever they make of it, not that the line round-trips; fidelity is the job of the
        // per-parser suites, which feed the corpus through the matching writer first.
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
        // Arrange — this used to be the ONE deliberate divergence: the patcher unfolded the escape
        // while the TMS kept the file representation. ADR-0039 made the escape a property of the
        // file rather than of one caller, so both readers now answer with the raw text. No naughty
        // string carries such a sequence, so these cases are composed by hand.
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
        // Arrange — the golden fixture IS the contract (CLAUDE.md), so it is driven through BOTH
        // parsers rather than only the TMS one. The patcher parser is per-line, so comments and
        // blank lines are filtered the way its own ParseFile does.
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
        // The two copies of the escape rule (ADR-0039) are duplicated by design — the contexts share
        // the file, never code — so nothing but this test stops one of them from drifting. The twin
        // per-context suites would both stay green through a one-sided change; this one would not.
        PatcherEscaper.Escape(naughty).ShouldBe(TmsEscaper.Escape(naughty));
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.DelimiterHazards), MemberType = typeof(NaughtyStringCases))]
    public void BothCarvers_OnTheSameLine_ShouldFindIdenticalFieldBoundaries(string naughty)
    {
        // Arrange — the second duplicated-by-design rule (ADR-0042), guarded the same way the escape
        // is: the content is followed by a raw trailing pipe, the exact collision #597 was about.
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
        // Arrange — the harder half: EMPTY args columns put the separator pairs next to each other,
        // so a copy whose backward search let a match straddle its slice edge would take the wrong
        // pair. With NULL args that mistake is invisible, which is why it needs its own theory.
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
        // A column that is neither NULL nor "1-2-3" is a malformed row on BOTH sides (ADR-0042).
        // One side swallowing it while the other stores it verbatim is exactly the divergence #597
        // produced once content leaked into the args boundary.
        new TranslationFileParser().ParseLine(line).IsFailure.ShouldBeTrue();

        ParsedExport tmsExport = await ParseAsync(line);
        tmsExport.Rows.ShouldBeEmpty();
        tmsExport.Errors.ShouldHaveSingleItem();
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public async Task PatcherExporterEscape_ThenTmsImportParse_ShouldRecoverTheOriginal(string naughty)
    {
        // Arrange — THE production direction: the patcher writes exported.txt, the TMS reads it.
        // Every other suite covers one context's writer against its own reader, or the TMS writer
        // against the patcher reader; this is the leg that carries every real import.
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
        // Arrange — the naughty corpus carries no real newline, no escape sequence and no separator
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

    /// <summary>Rebuilds the verbatim args string the TMS keeps from the patcher's 0-indexed int array.</summary>
    private static string ReconstructArgs(int[]? args)
        => args is null ? AbsentArgs : string.Join('-', args.Select(value => value + 1));

    /// <summary>
    /// The patcher collapses every spelling of "no arguments" to a null array, while the TMS keeps
    /// the column's file form. Comparing the raw strings would therefore make an EMPTY args column
    /// permanently inexpressible here — and an empty column next to a trailing pipe is exactly the
    /// boundary the backward pass slices for (ADR-0042), so the guard has to reach it.
    /// </summary>
    private static string NormalizeArgs(string args)
        => string.IsNullOrWhiteSpace(args) || args.Equals(AbsentArgs, StringComparison.OrdinalIgnoreCase)
            ? AbsentArgs
            : args;
}
