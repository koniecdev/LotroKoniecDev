using System.Text;
using LotroKoniecDev.TranslationSystem.API.Parsing;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

public sealed class TranslationExportParserTests
{
    private static async Task<ParsedExport> ParseAsync(string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        return await new TranslationExportParser().ParseAsync(stream, CancellationToken.None);
    }

    private static async Task<ParsedExport> ParseFixtureAsync(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        await using FileStream stream = File.OpenRead(path);
        return await new TranslationExportParser().ParseAsync(stream, CancellationToken.None);
    }

    private static async Task<ParsedExport> ParseBytesAsync(byte[] content)
    {
        using MemoryStream stream = new(content);
        return await new TranslationExportParser().ParseAsync(stream, CancellationToken.None);
    }

    [Fact]
    public async Task ParseAsync_WithGoldenFixture_ShouldParseEveryRowWithoutErrors()
    {
        // Act
        ParsedExport result = await ParseFixtureAsync("exported-sample.txt");

        // Assert: the comments and the blank line are skipped, nine rows remain.
        result.HasErrors.ShouldBeFalse();
        result.Rows.Count.ShouldBe(9);
        result.Rows[0].FileId.ShouldBe(620756992);
        result.Rows[0].GossipId.ShouldBe(1001);
        result.Rows[0].Content.ShouldBe("Witaj w Srodziemiu!");
        result.Rows[0].ArgsOrder.ShouldBe("NULL");
    }

    [Fact]
    public async Task ParseAsync_WithGoldenFixture_ShouldUnfoldTheEscapeIntoRawText()
    {
        // Act
        ParsedExport result = await ParseFixtureAsync("exported-sample.txt");

        // Assert: the catalog stores raw text, never the file representation (ADR-0039). The
        // backslash row is what proves the transform is injective: it must NOT become a newline.
        result.Rows.Single(row => row.GossipId == 1006).Content
            .ShouldBe("Wiersz jeden\nWiersz dwa\r\nWiersz trzy");
        result.Rows.Single(row => row.GossipId == 1007).Content
            .ShouldBe(@"Sciezka C:\notes i sekwencja \n");
    }

    [Fact]
    public async Task ParseAsync_WithGoldenFixture_ShouldKeepTrailingPipesOutOfTheArgsColumns()
    {
        // Act
        ParsedExport result = await ParseFixtureAsync("exported-sample.txt");

        // Assert: the separator is not escaped; the boundary is recovered by scanning backward, so
        // the run's last two pipes are the separator and every earlier one is content (ADR-0042).
        ParsedExportRow singlePipe = result.Rows.Single(row => row.GossipId == 1008);
        singlePipe.Content.ShouldBe("Koniec rury|");
        singlePipe.ArgsOrder.ShouldBe("NULL");

        ParsedExportRow triplePipe = result.Rows.Single(row => row.GossipId == 1009);
        triplePipe.Content.ShouldBe("Trzy rury|||");
        triplePipe.ArgsOrder.ShouldBe("1-2");
        triplePipe.ArgsId.ShouldBe("3-4");
    }

    [Fact]
    public async Task ParseAsync_WithSeparatorInsideContent_ShouldAnchorBothEndsAndReconstructContent()
    {
        // Act
        ParsedExport result = await ParseAsync("620756992||1003||Line with || inside the content||NULL||NULL||1");

        // Assert
        result.HasErrors.ShouldBeFalse();
        result.Rows.Count.ShouldBe(1);
        result.Rows[0].Content.ShouldBe("Line with || inside the content");
        result.Rows[0].ArgsOrder.ShouldBe("NULL");
        result.Rows[0].Approved.ShouldBeTrue();
    }

    [Fact]
    public async Task ParseAsync_WithSeparatorInContentAndArguments_ShouldAnchorTrailingFields()
    {
        // Act
        ParsedExport result = await ParseAsync("620756992||1004||Multi||arg||content||1-2||1-2||1");

        // Assert
        result.Rows.Count.ShouldBe(1);
        result.Rows[0].Content.ShouldBe("Multi||arg||content");
        result.Rows[0].ArgsOrder.ShouldBe("1-2");
        result.Rows[0].ArgsId.ShouldBe("1-2");
    }

    [Fact]
    public async Task ParseAsync_WithCommentAndBlankLines_ShouldSkipThem()
    {
        // Act
        ParsedExport result = await ParseAsync("# a comment\n\n620756992||1001||Text||NULL||NULL||1\n   \n");

        // Assert
        result.HasErrors.ShouldBeFalse();
        result.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ParseAsync_WithTruncatedFixture_ShouldReportEveryUnparseableLine()
    {
        // Act
        ParsedExport result = await ParseFixtureAsync("exported-truncated.txt");

        // Assert: one good line, two failures (too few fields, non-numeric file id).
        result.HasErrors.ShouldBeTrue();
        result.Rows.Count.ShouldBe(1);
        result.Errors.Count.ShouldBe(2);
        result.Errors[0].LineNumber.ShouldBe(2);
        result.Errors[1].LineNumber.ShouldBe(3);
    }

    [Fact]
    public async Task ParseAsync_WithTooFewFields_ShouldReportError()
    {
        // Act
        ParsedExport result = await ParseAsync("620756992||1001||only three fields");

        // Assert
        result.HasErrors.ShouldBeTrue();
        result.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseAsync_WithNonNumericGossipId_ShouldReportError()
    {
        // Act
        ParsedExport result = await ParseAsync("620756992||notanumber||Text||NULL||NULL||1");

        // Assert
        result.HasErrors.ShouldBeTrue();
        result.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseAsync_WithPolishDiacritics_ShouldPreserveContent()
    {
        // Act
        ParsedExport result = await ParseAsync("620756992||1001||Zazolc gesla jazn: Zażółć gęślą jaźń||NULL||NULL||1");

        // Assert
        result.HasErrors.ShouldBeFalse();
        result.Rows[0].Content.ShouldBe("Zazolc gesla jazn: Zażółć gęślą jaźń");
    }

    [Fact]
    public async Task ParseAsync_WithUtf8Bom_ShouldStripItAndParseTheFirstRow()
    {
        // Arrange: the patcher writes UTF-8, which can carry a BOM; it must not corrupt the file id.
        byte[] content = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("620756992||1001||Witaj||NULL||NULL||1")];

        // Act
        ParsedExport result = await ParseBytesAsync(content);

        // Assert
        result.HasErrors.ShouldBeFalse();
        result.Rows.Count.ShouldBe(1);
        result.Rows[0].FileId.ShouldBe(620756992);
    }

    [Fact]
    public async Task ParseAsync_WithInvalidUtf8Bytes_ShouldRejectTheUpload()
    {
        // Arrange: a stray 0xFF is never valid UTF-8. A wrong-charset/corrupt upload must be rejected,
        // not silently mis-decoded into content the diff would then treat as a mass source change.
        byte[] content =
        [
            .. Encoding.ASCII.GetBytes("620756992||1001||"),
            0xFF,
            .. Encoding.ASCII.GetBytes("||NULL||NULL||1"),
        ];

        // Act
        ParsedExport result = await ParseBytesAsync(content);

        // Assert
        result.HasErrors.ShouldBeTrue();
        result.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParseLinesAsync_WithMixedContent_ShouldStreamRowsAndErrorsInFileOrder()
    {
        // Arrange: comment, valid row, unparseable line, valid row: the stream must interleave
        // rows and errors exactly as the file reads, with 1-based line numbers.
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
    public async Task ParseAsync_WithTheSevenColumnGoldenFixture_ShouldParseEveryRowAndKeepItsDigest()
    {
        // Act: the seven-column golden fixture (ADR-0047). Its digests were computed outside both
        // implementations, so a row surviving here means the parser AND the verification agree with
        // the contract rather than merely with themselves.
        ParsedExport parsed = await ParseFixtureAsync("exported-sample-digested.txt");

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.Count.ShouldBe(9);
        parsed.Rows.ShouldAllBe(row => row.SourceDigest != null);
        parsed.Rows[0].SourceDigest.ShouldBe("a37cc1683216cd32");
        parsed.Rows[0].Content.ShouldBe("Witaj w Srodziemiu!");
    }

    [Fact]
    public async Task ParseAsync_BothGoldenFixtures_ShouldAgreeOnEveryContentField()
    {
        // Act: the two fixtures are the same rows at the two widths, so the seventh column must
        // change nothing about how the first six are read.
        ParsedExport sixColumn = await ParseFixtureAsync("exported-sample.txt");
        ParsedExport sevenColumn = await ParseFixtureAsync("exported-sample-digested.txt");

        // Assert
        sevenColumn.Rows.Select(row => row.Content).ShouldBe(sixColumn.Rows.Select(row => row.Content));
        sevenColumn.Rows.Select(row => row.ArgsOrder).ShouldBe(sixColumn.Rows.Select(row => row.ArgsOrder));
        sevenColumn.Rows.Select(row => row.ArgsId).ShouldBe(sixColumn.Rows.Select(row => row.ArgsId));
        sevenColumn.Rows.Select(row => row.Approved).ShouldBe(sixColumn.Rows.Select(row => row.Approved));
        sixColumn.Rows.ShouldAllBe(row => row.SourceDigest == null);
    }

    [Fact]
    public async Task ParseAsync_SixColumnUpload_ShouldStillImportWithoutADigest()
    {
        // Act: an older export or a hand-made file. A missing digest is "no parity check for this
        // row" on the import side; refusing it would break every existing exported.txt.
        ParsedExport parsed = await ParseAsync("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1\r\n");

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().SourceDigest.ShouldBeNull();
    }

    [Theory]
    [InlineData("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||0000000000000000")]
    [InlineData("620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||eacc6a53f9a2ae91")]
    [InlineData("620756992||1002||Tekst z <--DO_NOT_TOUCH!--> argumentem||1||1||1||a37cc1683216cd32")]
    public async Task ParseAsync_SevenColumnUploadWhoseDigestDoesNotMatchTheRow_ShouldRejectThatRow(string line)
    {
        // Act: a wrong-file upload, or a drift between the two contexts' digest implementations.
        // It has to fail HERE, loudly, instead of shipping an artifact whose every row every
        // player's patcher would then refuse as "source moved" (ADR-0047 §2).
        ParsedExport parsed = await ParseAsync($"{line}\r\n");

        // Assert
        parsed.Rows.ShouldBeEmpty();
        parsed.Errors.ShouldHaveSingleItem().Message.ShouldContain("source_digest");
    }

    [Theory]
    [InlineData("a37cc1683216cd32")]
    [InlineData("A37CC1683216CD32")]
    public async Task ParseAsync_SevenColumnUploadWithAMatchingDigest_ShouldAcceptItInEitherCase(string digest)
    {
        // Act: writers emit lowercase; a hand-edited file that upper-cased the column is still
        // unambiguously the same digest, so rejecting it would be pedantry with a real cost.
        ParsedExport parsed = await ParseAsync($"620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1||{digest}\r\n");

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().SourceDigest.ShouldBe(digest);
    }

    [Fact]
    public async Task ParseAsync_SevenColumnRowWhoseArgsColumnsAreBlankRatherThanNull_ShouldStillVerify()
    {
        // Act: the verification hashes the row the way the CATALOG will store it, so blank and
        // NULL must both normalize to an absent column. Hashing the literal would reject a row the
        // import then stores under a different triple.
        ParsedExport parsed = await ParseAsync("620756992||1001||Witaj w Srodziemiu!||||||1||a37cc1683216cd32\r\n");

        // Assert
        parsed.Errors.ShouldBeEmpty();
        parsed.Rows.ShouldHaveSingleItem().SourceDigest.ShouldBe("a37cc1683216cd32");
    }
}
