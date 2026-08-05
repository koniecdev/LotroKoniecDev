using LotroKoniecDev.Application.Parsers;

namespace LotroKoniecDev.Tests.Unit.Tests.Parsers;

/// <summary>
/// The patcher's copy of the field-boundary rule (ADR-0042). Its twin lives in the TMS suite over
/// the TMS' own copy, and <c>ParserContractParityTests</c> pins the two against each other — the
/// contexts share the file, never code, so nothing else stops one copy from drifting.
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
        // Arrange — THE case the backward pass slices for. With empty args columns the separator
        // pairs sit adjacent, so a search bound that let a match straddle the slice edge would take
        // the wrong pair and drag pipes out of the content. Every other test uses NULL args, which
        // pads the columns apart and hides the bug entirely.
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
        // Act — the carver parses nothing and unescapes nothing; it only finds boundaries.
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
        // Act — the two passes must not cross. A run of nine pipes carries four separators, one
        // short, and the backward pass would otherwise reach behind the gossip id separator.
        bool carved = TranslationLineCarver.TryCarve(line, out CarvedTranslationLine? fields);

        // Assert
        carved.ShouldBeFalse();
        fields.ShouldBeNull();
    }

    [Fact]
    public void TryCarve_NullLine_ShouldThrow()
    {
        // Act & Assert — a null line is a programmer error, not a malformed file (house rule:
        // guards are for programmer errors, Results are for business failures).
        Should.Throw<ArgumentNullException>(() => TranslationLineCarver.TryCarve(null!, out _));
    }
}
