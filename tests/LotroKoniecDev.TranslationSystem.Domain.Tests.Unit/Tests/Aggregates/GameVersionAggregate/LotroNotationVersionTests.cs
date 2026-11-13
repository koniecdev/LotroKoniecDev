using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.GameVersionAggregate;

public sealed class LotroNotationVersionTests
{
    [Fact]
    public void Create_WithCanonicalValue_ShouldSucceedAndRoundTrip()
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create("47.1.1");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("47.1.1");
    }

    [Fact]
    public void Create_WithSurroundingWhitespace_ShouldTrimValue()
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create("  47.1.1  ");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("47.1.1");
    }

    [Theory]
    [InlineData("48", "48")]
    [InlineData("48.0", "48")]
    [InlineData("48.0.0", "48")]
    [InlineData("47.1", "47.1")]
    [InlineData("47.1.0", "47.1")]
    [InlineData("47.1.1", "47.1.1")]
    [InlineData("47.0.1", "47.0.1")]
    [InlineData("0", "0")]
    [InlineData("0.0", "0")]
    [InlineData("0.0.0", "0")]
    [InlineData("047", "47")]
    [InlineData("48.01", "48.1")]
    [InlineData("001.001.001", "1.1.1")]
    [InlineData("000.001.000", "0.1")]
    [InlineData("000.000.000", "0")]
    [InlineData("123.456.789", "123.456.789")]
    public void Create_WithVariousNotations_ShouldNormalizeToCanonicalValue(string input, string expectedCanonical)
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(input);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(expectedCanonical);
    }

    [Theory]
    [InlineData("48", "48.0")]
    [InlineData("48", "48.0.0")]
    [InlineData("48.0", "48.0.0")]
    [InlineData("47.1", "47.1.0")]
    [InlineData("47.1", "47.1.000")]
    [InlineData("001.001.000", "1.1")]
    public void Create_WithTrailingZeroEquivalents_ShouldProduceEqualValueObjects(string left, string right)
    {
        // Act
        LotroNotationVersion first = LotroNotationVersion.Create(left).Value;
        LotroNotationVersion second = LotroNotationVersion.Create(right).Value;

        // Assert
        (first == second).ShouldBeTrue();
        first.Equals(second).ShouldBeTrue();
        first.Value.ShouldBe(second.Value);
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Theory]
    [InlineData("48", "47")]
    [InlineData("48.1", "48.2")]
    [InlineData("47.0.1", "47.1")]
    [InlineData("48.0.1", "48")]
    [InlineData("00.0.1", "1")]
    public void Create_WithDifferentVersions_ShouldProduceUnequalValueObjects(string left, string right)
    {
        // Act
        LotroNotationVersion first = LotroNotationVersion.Create(left).Value;
        LotroNotationVersion second = LotroNotationVersion.Create(right).Value;

        // Assert
        (first == second).ShouldBeFalse();
        first.Equals(second).ShouldBeFalse();
        first.Value.ShouldNotBe(second.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithNullOrWhitespace_ShouldReturnNullOrEmptyError(string? value)
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(value!);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.VersionProperty.NullOrEmpty);
    }

    [Fact]
    public void Create_WithTooLongValue_ShouldReturnTooLongError()
    {
        // Arrange
        string value = new('1', LotroNotationVersion.VersionMaxLength + 1);

        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(value);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.VersionProperty.LongerThanAllowed);
    }

    [Fact]
    public void Create_WithSingleSegmentAtMaxLength_ShouldSucceed()
    {
        // Arrange
        string value = new('1', LotroNotationVersion.VersionMaxLength);

        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(value);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData("47.1.0.0")]
    [InlineData("1.2.3.4")]
    [InlineData("48.1.2.3.4")]
    [InlineData("0.0.0.0")]
    public void Create_WithMoreSegmentsThanAllowed_ShouldReturnMoreSegmentsThanAllowedError(string value)
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(value);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.VersionProperty.MoreSegmentsThanAllowed);
        result.Error.Type.ShouldBe(TypeOfError.Validation);
    }

    [Fact]
    public void Create_WithThreeSegmentsLongerThanMaxLength_ShouldReturnTooLongError()
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create("123.456.7890");

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.VersionProperty.LongerThanAllowed);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("48.x")]
    [InlineData("v48")]
    [InlineData("48-0")]
    [InlineData("48,0")]
    [InlineData("48..0")]
    [InlineData(".48")]
    [InlineData("48.")]
    [InlineData(".")]
    [InlineData("48.0.")]
    [InlineData("48 0")]
    [InlineData("48.0 beta")]
    public void Create_WithInvalidFormat_ShouldReturnInvalidFormatValidationError(string value)
    {
        // Act
        Result<LotroNotationVersion> result = LotroNotationVersion.Create(value);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameVersionEntity.VersionProperty.InvalidFormat);
        result.Error.Type.ShouldBe(TypeOfError.Validation);
    }

    [Theory]
    [InlineData("48.10", "48.9")]
    [InlineData("100", "49")]
    [InlineData("47.1", "47.0.9")]
    [InlineData("47.0.1", "47")]
    [InlineData("48.0.1", "48")]
    [InlineData("48.1", "48")]
    [InlineData("49", "48.999")]
    [InlineData("48.9", "48.8")]
    [InlineData("1", "0")]
    [InlineData("0.1", "0")]
    [InlineData("10", "9")]
    [InlineData("123.456.789", "123.456.788")]
    [InlineData("99999999999", "99999999998")]
    [InlineData("10000000000", "9999999999")]
    [InlineData("99999999999", "1.1.1")]
    public void CompareTo_WithHigherNumberFirst_ShouldBePositiveAndNegativeWhenSwapped(string higher, string lower)
    {
        // Arrange
        LotroNotationVersion higherVersion = LotroNotationVersion.Create(higher).Value;
        LotroNotationVersion lowerVersion = LotroNotationVersion.Create(lower).Value;

        // Act
        int higherFirst = higherVersion.CompareTo(lowerVersion);
        int lowerFirst = lowerVersion.CompareTo(higherVersion);

        // Assert
        higherFirst.ShouldBePositive();
        lowerFirst.ShouldBeNegative();
    }

    [Theory]
    [InlineData("48", "48.0", true)]
    [InlineData("48", "48.0.0", true)]
    [InlineData("48.0", "48.0.0", true)]
    [InlineData("047", "47", true)]
    [InlineData("47.1", "47.1.0", true)]
    [InlineData("0", "0.0.0", true)]
    [InlineData("99999999999", "99999999999", true)]
    [InlineData("48", "48.0.1", false)]
    [InlineData("48.1", "48.10", false)]
    [InlineData("1.1", "11", false)]
    [InlineData("47.0.1", "47.1", false)]
    [InlineData("99999999999", "99999999998", false)]
    public void CompareTo_WithPairOfVersions_ShouldReturnZeroExactlyWhenEqual(string left, string right, bool expectedEqual)
    {
        // Arrange
        LotroNotationVersion first = LotroNotationVersion.Create(left).Value;
        LotroNotationVersion second = LotroNotationVersion.Create(right).Value;

        // Act
        int firstToSecond = first.CompareTo(second);
        int secondToFirst = second.CompareTo(first);

        // Assert
        (firstToSecond is 0).ShouldBe(expectedEqual);
        (secondToFirst is 0).ShouldBe(expectedEqual);
        first.Equals(second).ShouldBe(expectedEqual);
        (first == second).ShouldBe(expectedEqual);
    }

    [Fact]
    public void CompareTo_WithNull_ShouldBePositive()
    {
        // Arrange
        LotroNotationVersion version = LotroNotationVersion.Create("48").Value;

        // Act
        int result = version.CompareTo(null);

        // Assert
        result.ShouldBePositive();
    }

    [Theory]
    [InlineData("47", "48", true)]
    [InlineData("48", "48.0", false)]
    [InlineData("48.10", "48.9", false)]
    [InlineData(null, "48", true)]
    [InlineData("48", null, false)]
    [InlineData(null, null, false)]
    public void LessThanOperator_WithPairOfVersions_ShouldReturnExpected(string? left, string? right, bool expected)
    {
        // Arrange
        LotroNotationVersion? first = CreateOrNull(left);
        LotroNotationVersion? second = CreateOrNull(right);

        // Act
        bool result = first < second;

        // Assert
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("47", "48", true)]
    [InlineData("48", "48.0", true)]
    [InlineData("48.10", "48.9", false)]
    [InlineData(null, "48", true)]
    [InlineData("48", null, false)]
    [InlineData(null, null, true)]
    public void LessThanOrEqualOperator_WithPairOfVersions_ShouldReturnExpected(string? left, string? right, bool expected)
    {
        // Arrange
        LotroNotationVersion? first = CreateOrNull(left);
        LotroNotationVersion? second = CreateOrNull(right);

        // Act
        bool result = first <= second;

        // Assert
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("47", "48", false)]
    [InlineData("48", "48.0", false)]
    [InlineData("48.10", "48.9", true)]
    [InlineData(null, "48", false)]
    [InlineData("48", null, true)]
    [InlineData(null, null, false)]
    public void GreaterThanOperator_WithPairOfVersions_ShouldReturnExpected(string? left, string? right, bool expected)
    {
        // Arrange
        LotroNotationVersion? first = CreateOrNull(left);
        LotroNotationVersion? second = CreateOrNull(right);

        // Act
        bool result = first > second;

        // Assert
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("47", "48", false)]
    [InlineData("48", "48.0", true)]
    [InlineData("48.10", "48.9", true)]
    [InlineData(null, "48", false)]
    [InlineData("48", null, true)]
    [InlineData(null, null, true)]
    public void GreaterThanOrEqualOperator_WithPairOfVersions_ShouldReturnExpected(string? left, string? right, bool expected)
    {
        // Arrange
        LotroNotationVersion? first = CreateOrNull(left);
        LotroNotationVersion? second = CreateOrNull(right);

        // Act
        bool result = first >= second;

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void Order_WithShuffledVersions_ShouldSortByNumber()
    {
        // Arrange
        List<LotroNotationVersion> versions =
        [
            .. new[] { "100", "48.10", "47", "48.9", "47.0.9", "47.1", "48.0", "49", "47.0.1" }
                .Select(value => LotroNotationVersion.Create(value).Value),
        ];

        // Act
        List<string> sorted = versions.Order().Select(version => version.Value).ToList();

        // Assert
        sorted.ShouldBe(["47", "47.0.1", "47.0.9", "47.1", "48", "48.9", "48.10", "49", "100"]);
    }

    private static LotroNotationVersion? CreateOrNull(string? value) =>
        value is null ? null : LotroNotationVersion.Create(value).Value;
}
