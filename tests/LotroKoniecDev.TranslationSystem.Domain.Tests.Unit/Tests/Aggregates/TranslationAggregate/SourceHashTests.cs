using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslationAggregate;

public sealed class SourceHashTests
{
    [Fact]
    public void Compute_SameTriple_ShouldBeDeterministicAndEqual()
    {
        // Act
        SourceHash first = SourceHash.Compute("Hail <--DO_NOT_TOUCH!--> friend", "2-1", "1-1");
        SourceHash second = SourceHash.Compute("Hail <--DO_NOT_TOUCH!--> friend", "2-1", "1-1");

        // Assert
        first.ShouldBe(second);
    }

    [Theory]
    [InlineData("Text A", "Text B")]
    [InlineData("Text", "text")]
    [InlineData("Text", "Text ")]
    [InlineData("", " ")]
    public void Compute_DifferentText_ShouldDiffer(string firstText, string secondText)
    {
        // Act
        SourceHash first = SourceHash.Compute(firstText, null, null);
        SourceHash second = SourceHash.Compute(secondText, null, null);

        // Assert
        first.ShouldNotBe(second);
    }

    [Fact]
    public void Compute_NullArgsVersusEmptyArgs_ShouldDiffer()
    {
        // Arrange: the null-marker framing keeps an absent field distinct from an empty one.
        SourceHash withNull = SourceHash.Compute("Text", null, null);
        SourceHash withEmpty = SourceHash.Compute("Text", string.Empty, string.Empty);

        // Assert
        withNull.ShouldNotBe(withEmpty);
    }

    [Fact]
    public void Compute_FieldBoundaryShift_ShouldDiffer()
    {
        // Arrange: length framing: the concatenated bytes are identical, the field split is not.
        SourceHash first = SourceHash.Compute("ab", "c", null);
        SourceHash second = SourceHash.Compute("a", "bc", null);

        // Assert
        first.ShouldNotBe(second);
    }

    [Fact]
    public void Compute_ArgsSwappedBetweenColumns_ShouldDiffer()
    {
        // Act
        SourceHash first = SourceHash.Compute("Text", "1-2", null);
        SourceHash second = SourceHash.Compute("Text", null, "1-2");

        // Assert
        first.ShouldNotBe(second);
    }

    [Fact]
    public void Compute_FromValueObjectAndFromStoredColumns_ShouldMatch()
    {
        // Arrange: the incoming side hashes through the VO (which normalizes the raw "NULL" args
        // column to null); the catalog side hashes the stored columns, which were written from the
        // VO. Both must land on the same hash or the diff would report phantom source changes.
        TranslationSource source = TranslationSource.Create("Witaj w Srodziemiu!", "NULL", "NULL").Value;

        // Act
        SourceHash fromValueObject = SourceHash.Compute(source);
        SourceHash fromStoredColumns = SourceHash.Compute("Witaj w Srodziemiu!", null, null);

        // Assert
        fromValueObject.ShouldBe(fromStoredColumns);
    }

    [Fact]
    public void Compute_EmptyText_ShouldBeLegalAndDeterministic()
    {
        // Arrange: empty fragments are legal game content and must round-trip (TranslationSource).
        SourceHash first = SourceHash.Compute(string.Empty, null, null);
        SourceHash second = SourceHash.Compute(string.Empty, null, null);

        // Assert
        first.ShouldBe(second);
    }

    [Fact]
    public void ComputeEcho_WithoutPolish_ShouldBeNull()
    {
        // Act: an untranslated row has nothing of ours that could echo back from a patched DAT.
        SourceHash? echo = SourceHash.ComputeEcho(null, "1-2", "1-2");

        // Assert
        echo.ShouldBeNull();
    }

    [Fact]
    public void ComputeEcho_WithPolish_ShouldEqualTheIncomingHashOfThePatchedTriple()
    {
        // Arrange: the incoming side of an echo is the exported row (our Polish as text, the
        // source's identity args), hashed through the VO like every upload row (spec 0012).
        TranslationSource echoedRow = TranslationSource.Create("Witaj <--DO_NOT_TOUCH!--> przyjacielu", "1-1", "1-1").Value;

        // Act
        SourceHash? echo = SourceHash.ComputeEcho("Witaj <--DO_NOT_TOUCH!--> przyjacielu", "1-1", "1-1");

        // Assert
        echo.ShouldBe(SourceHash.Compute(echoedRow));
    }

    [Fact]
    public void Compute_LargeText_ShouldHashWithoutError()
    {
        // Arrange: a multi-kilobyte fragment exercises the pooled-buffer path beyond typical sizes.
        string largeText = new('x', 100_000);

        // Act
        SourceHash first = SourceHash.Compute(largeText, "1-2-3", "1-1-1");
        SourceHash second = SourceHash.Compute(largeText, "1-2-3", "1-1-1");

        // Assert
        first.ShouldBe(second);
    }
}
