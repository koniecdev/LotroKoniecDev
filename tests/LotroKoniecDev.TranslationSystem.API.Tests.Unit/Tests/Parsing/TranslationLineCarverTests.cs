using LotroKoniecDev.TranslationSystem.API.Parsing;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

/// <summary>
/// The TMS copy of the carver (ADR-0042). Its twin suite covers the patcher's copy, and
/// <c>ParserContractParityTests</c> checks the two against each other.
/// </summary>
public sealed class TranslationLineCarverTests
{
    [Theory]
    [InlineData("abc|")]
    [InlineData("abc||")]
    [InlineData("abc|||")]
    [InlineData("|")]
    [InlineData("||")]
    [InlineData("|||")]
    [InlineData("|abc|")]
    [InlineData("")]
    public void TryCarve_ContentEndingInPipesWithEmptyArgsColumns_ShouldStillEndContentAtTheRightPipe(string content)
    {
        // Arrange: empty args put the separator pairs side by side, so a search that straddles its slice
        // edge takes the wrong pair. NULL args, as in every other test, hide that bug.
        string line = $"620756992||1001||{content}||||||1";

        // Act
        bool carved = TranslationLineCarver.TryCarve(line, out CarvedTranslationLine? fields);

        // Assert
        carved.ShouldBeTrue();
        fields!.Content.ShouldBe(content);
        fields.ArgsOrder.ShouldBe(string.Empty);
        fields.ArgsId.ShouldBe(string.Empty);
        fields.Approved.ShouldBe("1");
    }

    [Theory]
    [InlineData("620756992||1001||Witaj||NULL||NULL||1", "620756992", "1001", "Witaj", "NULL", "NULL", "1")]
    [InlineData("1||2||||NULL||NULL||1", "1", "2", "", "NULL", "NULL", "1")]
    [InlineData("1||2||a||b||1-2||3-4||0", "1", "2", "a||b", "1-2", "3-4", "0")]
    [InlineData("1||2|||leading||NULL||NULL||1", "1", "2", "|leading", "NULL", "NULL", "1")]
    [InlineData("1||2||trailing|||NULL||NULL||1", "1", "2", "trailing|", "NULL", "NULL", "1")]
    [InlineData("1||2|||||NULL||NULL||1", "1", "2", "|", "NULL", "NULL", "1")]
    public void TryCarve_WellFormedLine_ShouldCarveEveryFieldVerbatim(
        string line, string fileId, string gossipId, string content, string argsOrder, string argsId, string approved)
    {
        // Act
        bool carved = TranslationLineCarver.TryCarve(line, out CarvedTranslationLine? fields);

        // Assert
        carved.ShouldBeTrue();
        fields!.FileId.ShouldBe(fileId);
        fields.GossipId.ShouldBe(gossipId);
        fields.Content.ShouldBe(content);
        fields.ArgsOrder.ShouldBe(argsOrder);
        fields.ArgsId.ShouldBe(argsId);
        fields.Approved.ShouldBe(approved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("100")]
    [InlineData("100||200")]
    [InlineData("100||200||content")]
    [InlineData("100||200||content||NULL")]
    [InlineData("100||200||content||NULL||NULL")]
    [InlineData("|||||||||")]
    public void TryCarve_LineWithoutFiveSeparatorsOutsideTheContent_ShouldRefuseToCarve(string line)
    {
        // Act: the passes must not cross. Nine pipes carry four separators, one short.
        bool carved = TranslationLineCarver.TryCarve(line, out CarvedTranslationLine? fields);

        // Assert
        carved.ShouldBeFalse();
        fields.ShouldBeNull();
    }

    [Fact]
    public void TryCarve_NullLine_ShouldThrow()
    {
        // Act & Assert: a programmer error, not a malformed file.
        Should.Throw<ArgumentNullException>(() => TranslationLineCarver.TryCarve(null!, out _));
    }

    [Theory]
    [InlineData("620756992||1001||Witaj||NULL||NULL||1||a37cc1683216cd32", "Witaj", "NULL", "1", "a37cc1683216cd32")]
    [InlineData("1||2||a||b||1-2||3-4||0||b37cc1683216cd32", "a||b", "1-2", "0", "b37cc1683216cd32")]
    [InlineData("1||2||trailing|||NULL||NULL||1||A37CC1683216CD32", "trailing|", "NULL", "1", "A37CC1683216CD32")]
    [InlineData("1||2||||||||1||00112233445566ff", "", "", "1", "00112233445566ff")]
    [InlineData("1||2||a37cc1683216cd32||NULL||NULL||1||b37cc1683216cd32", "a37cc1683216cd32", "NULL", "1", "b37cc1683216cd32")]
    public void TryCarve_SevenColumnLine_ShouldCarveTheSourceDigestWithoutDisturbingTheOtherFields(
        string line, string content, string argsOrder, string approved, string sourceDigest)
    {
        // Act: one more backward step, because the last field is 16 hex characters (ADR-0047 §2).
        bool carved = TranslationLineCarver.TryCarve(line, out CarvedTranslationLine? fields);

        // Assert
        carved.ShouldBeTrue();
        fields!.Content.ShouldBe(content);
        fields.ArgsOrder.ShouldBe(argsOrder);
        fields.Approved.ShouldBe(approved);
        fields.SourceDigest.ShouldBe(sourceDigest);
    }

    [Theory]
    [InlineData("620756992||1001||Witaj||NULL||NULL||1")]
    [InlineData("620756992||1001||Witaj||NULL||NULL||0")]
    [InlineData("1||2||a||b||1-2||3-4||1")]
    public void TryCarve_SixColumnLine_ShouldCarveExactlyAsBeforeWithNoSourceDigest(string line)
    {
        // Act: ADR-0047 makes the seventh column optional on read, not on write.
        bool carved = TranslationLineCarver.TryCarve(line, out CarvedTranslationLine? fields);

        // Assert
        carved.ShouldBeTrue();
        fields!.SourceDigest.ShouldBeNull();
    }

    [Theory]
    [InlineData("1||2||content||NULL||NULL||a37cc1683216cd3", "a37cc1683216cd3")]
    [InlineData("1||2||content||NULL||NULL||a37cc1683216cd3g", "a37cc1683216cd3g")]
    [InlineData("1||2||content||NULL||NULL||not-a-digest", "not-a-digest")]
    public void TryCarve_LastFieldThatOnlyResemblesADigest_ShouldStayTheApprovedColumn(string line, string approved)
    {
        // Act: anything short of exactly 16 hex characters falls back to six columns.
        bool carved = TranslationLineCarver.TryCarve(line, out CarvedTranslationLine? fields);

        // Assert
        carved.ShouldBeTrue();
        fields!.Approved.ShouldBe(approved);
        fields.SourceDigest.ShouldBeNull();
    }

    [Theory]
    [InlineData("620756992||1001||Witaj||NULL||NULL||1||a37cc1683216cd32 ")]
    [InlineData("620756992||1001||Witaj||NULL||NULL||1||a37cc1683216cd32\t")]
    [InlineData("620756992||1001||Witaj||NULL||NULL||1||a37cc1683216cd32\r")]
    [InlineData("620756992||1001||Witaj||NULL||NULL||1||a37cc1683216cd32 \t")]
    public void TryCarve_DigestFollowedByTrailingWhitespace_ShouldStillCarveSevenColumns(string line)
    {
        // Act: as six columns, the content would swallow "||NULL" and be stored as the source.
        bool carved = TranslationLineCarver.TryCarve(line, out CarvedTranslationLine? fields);

        // Assert
        carved.ShouldBeTrue();
        fields!.Content.ShouldBe("Witaj");
        fields.ArgsOrder.ShouldBe("NULL");
        fields.ArgsId.ShouldBe("NULL");
        fields.Approved.ShouldBe("1");
        fields.SourceDigest.ShouldBe("a37cc1683216cd32");
    }

    [Fact]
    public void TryCarve_DigestWithoutEnoughSeparatorsBeforeIt_ShouldRefuseToCarve()
    {
        // Act: no room for the args columns, and taking the content's separator would mis-carve it.
        bool carved = TranslationLineCarver.TryCarve("1||2||content||NULL||a37cc1683216cd32", out CarvedTranslationLine? fields);

        // Assert
        carved.ShouldBeFalse();
        fields.ShouldBeNull();
    }
}
