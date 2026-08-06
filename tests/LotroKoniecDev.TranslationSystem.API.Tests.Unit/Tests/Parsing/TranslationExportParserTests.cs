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

        // Assert — the comments and the blank line are skipped, nine rows remain.
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

        // Assert — the catalog stores raw text, never the file representation (ADR-0039). The
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

        // Assert — the separator is not escaped; the boundary is recovered by scanning backward, so
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

        // Assert — one good line, two failures (too few fields, non-numeric file id).
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
        // Arrange — the patcher writes UTF-8, which can carry a BOM; it must not corrupt the file id.
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
        // Arrange — a stray 0xFF is never valid UTF-8. A wrong-charset/corrupt upload must be rejected,
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
        // Arrange — comment, valid row, unparseable line, valid row: the stream must interleave
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
        // Arrange — a valid first line, then bytes that fail strict UTF-8 decoding.
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

        // Assert — the decode failure ends the stream; nothing after it is parseable anyway.
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
}
