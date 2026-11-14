using System.Text;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

public sealed class TranslationExportParserTests
{
    private static async Task<CollectedLines> ParseTextAsync(string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        return await new TranslationExportParser().ParseLinesAsync(stream, CancellationToken.None).CollectAsync();
    }

    private static async Task<CollectedLines> ParseFixtureAsync(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        await using FileStream stream = File.OpenRead(path);
        return await new TranslationExportParser().ParseLinesAsync(stream, CancellationToken.None).CollectAsync();
    }

    private static async Task<CollectedLines> ParseBytesAsync(byte[] content)
    {
        using MemoryStream stream = new(content);
        return await new TranslationExportParser().ParseLinesAsync(stream, CancellationToken.None).CollectAsync();
    }

    [Fact]
    public async Task ParseLinesAsync_WithGoldenFixture_ShouldParseEveryRowWithoutErrors()
    {
        // Act
        CollectedLines result = await ParseFixtureAsync("exported-sample.txt");

        // Assert: the comments and the blank line are skipped, nine rows remain.
        result.Errors.ShouldBeEmpty();
        result.Rows.Count.ShouldBe(9);
        result.Rows[0].FileId.ShouldBe(620756992);
        result.Rows[0].GossipId.ShouldBe(1001);
        result.Rows[0].Content.ShouldBe("Witaj w Srodziemiu!");
        result.Rows[0].ArgsOrder.ShouldBe("NULL");
    }

    [Fact]
    public async Task ParseLinesAsync_WithGoldenFixture_ShouldUnfoldTheEscapeIntoRawText()
    {
        // Act
        CollectedLines result = await ParseFixtureAsync("exported-sample.txt");

        // Assert: raw text (ADR-0039). The backslash row proves injectivity: it must not become a
        // newline.
        result.Rows.Single(row => row.GossipId == 1006).Content
            .ShouldBe("Wiersz jeden\nWiersz dwa\r\nWiersz trzy");
        result.Rows.Single(row => row.GossipId == 1007).Content
            .ShouldBe(@"Sciezka C:\notes i sekwencja \n");
    }

    [Fact]
    public async Task ParseLinesAsync_WithGoldenFixture_ShouldKeepTrailingPipesOutOfTheArgsColumns()
    {
        // Act
        CollectedLines result = await ParseFixtureAsync("exported-sample.txt");

        // Assert: scanning backward, a run's last two pipes are the separator (ADR-0042).
        ParsedExportRow singlePipe = result.Rows.Single(row => row.GossipId == 1008);
        singlePipe.Content.ShouldBe("Koniec rury|");
        singlePipe.ArgsOrder.ShouldBe("NULL");

        ParsedExportRow triplePipe = result.Rows.Single(row => row.GossipId == 1009);
        triplePipe.Content.ShouldBe("Trzy rury|||");
        triplePipe.ArgsOrder.ShouldBe("1-2");
        triplePipe.ArgsId.ShouldBe("3-4");
    }

    [Fact]
    public async Task ParseLinesAsync_WithSeparatorInsideContent_ShouldAnchorBothEndsAndReconstructContent()
    {
        // Act
        CollectedLines result = await ParseTextAsync("620756992||1003||Line with || inside the content||NULL||NULL||1");

        // Assert
        result.Errors.ShouldBeEmpty();
        result.Rows.Count.ShouldBe(1);
        result.Rows[0].Content.ShouldBe("Line with || inside the content");
        result.Rows[0].ArgsOrder.ShouldBe("NULL");
        result.Rows[0].Approved.ShouldBeTrue();
    }

    [Fact]
    public async Task ParseLinesAsync_WithSeparatorInContentAndArguments_ShouldAnchorTrailingFields()
    {
        // Act
        CollectedLines result = await ParseTextAsync("620756992||1004||Multi||arg||content||1-2||1-2||1");

        // Assert
        result.Rows.Count.ShouldBe(1);
        result.Rows[0].Content.ShouldBe("Multi||arg||content");
        result.Rows[0].ArgsOrder.ShouldBe("1-2");
        result.Rows[0].ArgsId.ShouldBe("1-2");
    }

    [Fact]
    public async Task ParseLinesAsync_WithCommentAndBlankLines_ShouldSkipThem()
    {
        // Act
        CollectedLines result = await ParseTextAsync("# a comment\n\n620756992||1001||Text||NULL||NULL||1\n   \n");

        // Assert
        result.Errors.ShouldBeEmpty();
        result.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ParseLinesAsync_WithTruncatedFixture_ShouldReportEveryUnparseableLine()
    {
        // Act
        CollectedLines result = await ParseFixtureAsync("exported-truncated.txt");

        // Assert: one good line, two failures (too few fields, non-numeric file id).
        result.Rows.Count.ShouldBe(1);
        result.Errors.Count.ShouldBe(2);
        result.Errors[0].LineNumber.ShouldBe(2);
        result.Errors[1].LineNumber.ShouldBe(3);
    }

    [Fact]
    public async Task ParseLinesAsync_WithTooFewFields_ShouldReportError()
    {
        // Act
        CollectedLines result = await ParseTextAsync("620756992||1001||only three fields");

        // Assert
        result.Errors.ShouldHaveSingleItem();
        result.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseLinesAsync_WithNonNumericGossipId_ShouldReportError()
    {
        // Act
        CollectedLines result = await ParseTextAsync("620756992||notanumber||Text||NULL||NULL||1");

        // Assert
        result.Errors.ShouldHaveSingleItem();
        result.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseLinesAsync_WithPolishDiacritics_ShouldPreserveContent()
    {
        // Act
        CollectedLines result = await ParseTextAsync("620756992||1001||Zazolc gesla jazn: Zażółć gęślą jaźń||NULL||NULL||1");

        // Assert
        result.Errors.ShouldBeEmpty();
        result.Rows[0].Content.ShouldBe("Zazolc gesla jazn: Zażółć gęślą jaźń");
    }

    [Fact]
    public async Task ParseLinesAsync_WithUtf8Bom_ShouldStripItAndParseTheFirstRow()
    {
        // Arrange: the patcher writes UTF-8, which can carry a BOM; it must not corrupt the file id.
        byte[] content = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("620756992||1001||Witaj||NULL||NULL||1")];

        // Act
        CollectedLines result = await ParseBytesAsync(content);

        // Assert
        result.Errors.ShouldBeEmpty();
        result.Rows.Count.ShouldBe(1);
        result.Rows[0].FileId.ShouldBe(620756992);
    }

    [Fact]
    public async Task ParseLinesAsync_WithInvalidUtf8Bytes_ShouldRejectTheUpload()
    {
        // Arrange: a stray 0xFF is never valid UTF-8. Mis-decoded, it would read as a mass source change.
        byte[] content =
        [
            .. Encoding.ASCII.GetBytes("620756992||1001||"),
            0xFF,
            .. Encoding.ASCII.GetBytes("||NULL||NULL||1"),
        ];

        // Act
        CollectedLines result = await ParseBytesAsync(content);

        // Assert
        result.Errors.ShouldHaveSingleItem();
        result.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseLinesAsync_WithMixedContent_ShouldStreamRowsAndErrorsInFileOrder()
    {
        // Arrange: comment, row, broken line, row. Line numbers are 1-based.
        string content = "# comment\n620756992||1||Alpha||NULL||NULL||1\nbroken line\n620756992||2||Beta||NULL||NULL||1";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));

        // Act
        List<ParsedExportLine> lines = [];
        await foreach (ParsedExportLine line in new TranslationExportParser().ParseLinesAsync(stream, CancellationToken.None))
        {
            lines.Add(line);
        }

        // Assert
        lines.Count.ShouldBe(3);
        lines[0].Row!.GossipId.ShouldBe(1);
        lines[1].Error!.LineNumber.ShouldBe(3);
        lines[2].Row!.GossipId.ShouldBe(2);
    }

    [Fact]
    public async Task ParseLinesAsync_WithInvalidUtf8MidStream_ShouldYieldOneErrorAndStop()
    {
        // Arrange: a valid first line, then bytes that fail strict UTF-8 decoding.
        byte[] content =
        [
            .. Encoding.UTF8.GetBytes("620756992||1||Alpha||NULL||NULL||1\n"),
            0xFF,
            .. Encoding.ASCII.GetBytes("||garbage"),
        ];
        using MemoryStream stream = new(content);

        // Act
        List<ParsedExportLine> lines = [];
        await foreach (ParsedExportLine line in new TranslationExportParser().ParseLinesAsync(stream, CancellationToken.None))
        {
            lines.Add(line);
        }

        // Assert: the decode failure ends the stream; nothing after it is parseable anyway.
        lines[^1].Error.ShouldNotBeNull();
        lines[^1].Error!.Message.ShouldContain("not valid UTF-8");
        lines.Count(line => line.Error is not null).ShouldBe(1);
    }

    [Fact]
    public async Task ParseLinesAsync_WithEmptyStream_ShouldYieldNothing()
    {
        // Arrange
        using MemoryStream stream = new([]);

        // Act
        List<ParsedExportLine> lines = [];
        await foreach (ParsedExportLine line in new TranslationExportParser().ParseLinesAsync(stream, CancellationToken.None))
        {
            lines.Add(line);
        }

        // Assert
        lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseLinesAsync_WithTheSevenColumnGoldenFixture_ShouldParseEveryRowAndKeepItsDigest()
    {
        // Act: the digests were computed outside both implementations, so passing means agreement with
        // the contract, not just with itself (ADR-0047).
        CollectedLines parsed = await ParseFixtureAsync("exported-sample-digested.txt");

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.Count.ShouldBe(9);
        parsed.Rows.ShouldAllBe(row => row.SourceDigest != null);
        parsed.Rows[0].SourceDigest.ShouldBe("a37cc1683216cd32");
        parsed.Rows[0].Content.ShouldBe("Witaj w Srodziemiu!");
    }

    [Fact]
    public async Task ParseLinesAsync_BothGoldenFixtures_ShouldAgreeOnEveryContentField()
    {
        // Act: the same rows at both widths.
        CollectedLines sixColumn = await ParseFixtureAsync("exported-sample.txt");
        CollectedLines sevenColumn = await ParseFixtureAsync("exported-sample-digested.txt");

        // Assert
        sevenColumn.Rows.Select(row => row.Content).ShouldBe(sixColumn.Rows.Select(row => row.Content));
        sevenColumn.Rows.Select(row => row.ArgsOrder).ShouldBe(sixColumn.Rows.Select(row => row.ArgsOrder));
        sevenColumn.Rows.Select(row => row.ArgsId).ShouldBe(sixColumn.Rows.Select(row => row.ArgsId));
        sevenColumn.Rows.Select(row => row.Approved).ShouldBe(sixColumn.Rows.Select(row => row.Approved));
        sixColumn.Rows.ShouldAllBe(row => row.SourceDigest == null);
    }

    [Fact]
    public async Task ParseLinesAsync_SixColumnUpload_ShouldStillImportWithoutADigest()
    {
        // Act: refusing a six-column row would break every existing exported.txt.
        CollectedLines parsed = await ParseTextAsync("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1\r\n");

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().SourceDigest.ShouldBeNull();
    }

    [Theory]
    [InlineData("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||0000000000000000")]
    [InlineData("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||eacc6a53f9a2ae91")]
    [InlineData("620756992||1002||Tekst z <--DO_NOT_TOUCH!--> argumentem||1||1||1||a37cc1683216cd32")]
    public async Task ParseLinesAsync_SevenColumnUploadWhoseDigestDoesNotMatchTheRow_ShouldRejectThatRow(string line)
    {
        // Act: it must fail here, not as "source moved" on every player's machine (ADR-0047 §2).
        CollectedLines parsed = await ParseTextAsync($"{line}\r\n");

        // Assert
        parsed.Rows.ShouldBeEmpty();
        parsed.Errors.ShouldHaveSingleItem().Message.ShouldContain("source_digest");
    }

    [Theory]
    [InlineData("a37cc1683216cd32")]
    [InlineData("A37CC1683216CD32")]
    public async Task ParseLinesAsync_SevenColumnUploadWithAMatchingDigest_ShouldAcceptItInEitherCase(string digest)
    {
        // Act: a hand-edited upper-case digest is still the same digest.
        CollectedLines parsed = await ParseTextAsync($"620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||{digest}\r\n");

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().SourceDigest.ShouldBe(digest);
    }

    [Fact]
    public async Task ParseLinesAsync_SevenColumnRowWhoseArgsColumnsAreBlankRatherThanNull_ShouldStillVerify()
    {
        // Act: the check hashes the row the way the catalog stores it, so blank and NULL are both
        // absent.
        CollectedLines parsed = await ParseTextAsync("620756992||1001||Witaj w Srodziemiu!||||||1||a37cc1683216cd32\r\n");

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().SourceDigest.ShouldBe("a37cc1683216cd32");
    }
}
