using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class TranslationSearchTermParserTests
{
    [Theory]
    [InlineData("0", 0, 0L)]
    [InlineData("7", 7, 7L)]
    [InlineData("0005", 5, 5L)]
    [InlineData("620756992", 620756992, 620756992L)]
    [InlineData("2147483647", int.MaxValue, 2147483647L)]
    public void Parse_NumberThatFitsAnInt_ShouldNameBothTheFileIdAndTheGossipId(
        string term,
        int expectedFileId,
        long expectedGossipId)
    {
        // Act
        TranslationSearchIds ids = TranslationSearchTermParser.Parse(term);

        // Assert
        ids.ShouldBe(new TranslationSearchIds(expectedFileId, expectedGossipId, PairFileId: null, PairGossipId: null));
    }

    [Theory]
    [InlineData("2147483648", 2147483648L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void Parse_NumberTooBigForAnIntButNotForALong_ShouldNameOnlyTheGossipId(string term, long expectedGossipId)
    {
        // Act
        TranslationSearchIds ids = TranslationSearchTermParser.Parse(term);

        // Assert
        ids.ShouldBe(new TranslationSearchIds(FileId: null, expectedGossipId, PairFileId: null, PairGossipId: null));
    }

    [Theory]
    [InlineData("620756992:6396484", 620756992, 6396484L)]
    [InlineData("0:0", 0, 0L)]
    [InlineData("007:0009", 7, 9L)]
    [InlineData("2147483647:9223372036854775807", int.MaxValue, long.MaxValue)]
    public void Parse_FileIdColonGossipId_ShouldNameThePair(string term, int expectedFileId, long expectedGossipId)
    {
        // Act
        TranslationSearchIds ids = TranslationSearchTermParser.Parse(term);

        // Assert
        ids.ShouldBe(new TranslationSearchIds(FileId: null, GossipId: null, expectedFileId, expectedGossipId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("9223372036854775808")]
    [InlineData("99999999999999999999999")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData("5 6")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("1,000")]
    [InlineData("1.5")]
    [InlineData("5.0")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("5a")]
    [InlineData("5\0")]
    [InlineData("5\0\0")]
    [InlineData("2147483648:5")]
    [InlineData("5:9223372036854775808")]
    [InlineData("5:")]
    [InlineData(":5")]
    [InlineData(":")]
    [InlineData("5::6")]
    [InlineData("5:6:7")]
    [InlineData("5 :6")]
    [InlineData("5: 6")]
    [InlineData("5:+6")]
    [InlineData("-5:6")]
    [InlineData("5:6\0")]
    [InlineData("5/6")]
    [InlineData("5||6")]
    [InlineData("a:b")]
    public void Parse_TermThatIsNeitherAPlainNumberNorTheColonForm_ShouldNameNoId(string term)
    {
        // Act
        TranslationSearchIds ids = TranslationSearchTermParser.Parse(term);

        // Assert
        ids.ShouldBe(TranslationSearchIds.None);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.NonAsciiDigits), MemberType = typeof(NaughtyStringCases))]
    public void Parse_NonAsciiDigits_ShouldNameNoId(string naughtyDigits)
    {
        // Act
        TranslationSearchIds ids = TranslationSearchTermParser.Parse(naughtyDigits);

        // Assert
        ids.ShouldBe(TranslationSearchIds.None);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.NonAsciiDigits), MemberType = typeof(NaughtyStringCases))]
    public void Parse_NonAsciiDigitsInTheColonForm_ShouldNameNoId(string naughtyDigits)
    {
        // Act
        TranslationSearchIds ids = TranslationSearchTermParser.Parse($"{naughtyDigits}:{naughtyDigits}");

        // Assert
        ids.ShouldBe(TranslationSearchIds.None);
    }
}
