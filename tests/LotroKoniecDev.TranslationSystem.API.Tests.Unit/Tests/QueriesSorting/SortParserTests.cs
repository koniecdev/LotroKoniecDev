using LotroKoniecDev.TranslationSystem.API.QueriesSorting;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.QueriesSorting;

public sealed class SortParserTests
{
    [Fact]
    public void Parse_WhenSingleKeyWithoutOperand_DefaultsToAscending()
    {
        // Act
        List<SortItem> items = SortParser.Parse("fileId").ToList();

        // Assert
        SortItem item = items.ShouldHaveSingleItem();
        item.PropertyName.ShouldBe("fileId");
        item.Operand.ShouldBe(SortOperand.Asc);
    }

    [Fact]
    public void Parse_WhenDescendingOperand_ParsesDescending()
    {
        // Act
        List<SortItem> items = SortParser.Parse("status:desc").ToList();

        // Assert
        SortItem item = items.ShouldHaveSingleItem();
        item.PropertyName.ShouldBe("status");
        item.Operand.ShouldBe(SortOperand.Desc);
    }

    [Theory]
    [InlineData("DESC")]
    [InlineData("Desc")]
    [InlineData("dEsC")]
    public void Parse_OperandIsCaseInsensitive(string operand)
    {
        // Act
        List<SortItem> items = SortParser.Parse($"status:{operand}").ToList();

        // Assert
        items.ShouldHaveSingleItem().Operand.ShouldBe(SortOperand.Desc);
    }

    [Fact]
    public void Parse_WhenUnknownOperandText_DefaultsToAscending()
    {
        // Act
        List<SortItem> items = SortParser.Parse("fileId:sideways").ToList();

        // Assert
        items.ShouldHaveSingleItem().Operand.ShouldBe(SortOperand.Asc);
    }

    [Fact]
    public void Parse_WhenMultipleKeys_PreservesOrderAndOperands()
    {
        // Act
        List<SortItem> items = SortParser.Parse("status:desc,fileId:asc,gossipId").ToList();

        // Assert
        items.Count.ShouldBe(3);
        items[0].ShouldBe(new SortItem("status", SortOperand.Desc));
        items[1].ShouldBe(new SortItem("fileId", SortOperand.Asc));
        items[2].ShouldBe(new SortItem("gossipId", SortOperand.Asc));
    }

    [Fact]
    public void Parse_TrimsWhitespaceAroundKeyAndOperand()
    {
        // Act
        List<SortItem> items = SortParser.Parse("  status : desc  ").ToList();

        // Assert
        items.ShouldHaveSingleItem().ShouldBe(new SortItem("status", SortOperand.Desc));
    }

    [Fact]
    public void Parse_SkipsEmptyAndKeylessSegments()
    {
        // Arrange — a stray double comma yields an empty segment; ":desc" has no key.
        // Act
        List<SortItem> items = SortParser.Parse("fileId,,:desc, gossipId ").ToList();

        // Assert
        items.Count.ShouldBe(2);
        items[0].PropertyName.ShouldBe("fileId");
        items[1].PropertyName.ShouldBe("gossipId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData(" , , ")]
    public void Parse_WhenEmptyOrMalformed_ReturnsEmpty(string sort)
        => SortParser.Parse(sort).ShouldBeEmpty();
}
