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

        // Assert — the comment and the blank line are skipped, five rows remain.
        result.HasErrors.ShouldBeFalse();
        result.Rows.Count.ShouldBe(5);
        result.Rows[0].FileId.ShouldBe(620756992);
        result.Rows[0].GossipId.ShouldBe(1001);
        result.Rows[0].Content.ShouldBe("Witaj w Srodziemiu!");
        result.Rows[0].ArgsOrder.ShouldBe("NULL");
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
}
