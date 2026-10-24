using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslationAggregate;

/// <summary>
/// The TMS half of the <c>source_digest</c> contract (ADR-0047): framing, truncation and byte order are
/// wire facts, not internal details.
/// </summary>
public sealed class SourceHashWireDigestTests
{
    [Theory]
    [MemberData(nameof(SourceDigestGoldenCases.All), MemberType = typeof(SourceDigestGoldenCases))]
    public void ToWireDigest_OnTheGoldenTriple_ShouldProduceTheContractDigest(
        string text, string? argsOrder, string? argsId, string expected)
        // Its twin in the patcher's Tests.Unit checks the same fixture. Both must match a value neither
        // implementation produced, or a one-sided framing change makes every artifact unpatchable.
        => SourceHash.Compute(text, argsOrder, argsId).ToWireDigest().ShouldBe(expected);

    [Fact]
    public void ToWireDigest_ShouldEmitSixteenLowercaseHexCharacters()
        => SourceHash.Compute("Cokolwiek", null, null).ToWireDigest().ShouldMatch("^[0-9a-f]{16}$");

    [Fact]
    public void ToWireDigest_ShouldBeTheDigestBytesInDigestOrderNotTheUlongsHexRendering()
    {
        // Arrange: High holds the bytes little-endian, so x16 would reverse them into a well-formed
        // column no patcher matches, and only this assertion would notice.
        SourceHash hash = SourceHash.Compute("Witaj w Srodziemiu!", null, null);

        // Assert
        hash.ToWireDigest().ShouldNotBe(hash.High.ToString("x16"));
    }

    [Theory]
    [InlineData("a37cc1683216cd32", true)]
    [InlineData("A37CC1683216CD32", true)]
    [InlineData("a37cc1683216cd3", false)]
    [InlineData("a37cc1683216cd321", false)]
    [InlineData("a37cc1683216cd3g", false)]
    [InlineData("1", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWireDigest_ShouldRecogniseOnlySixteenHexCharacters(string? value, bool expected)
        // It tells a seventh column from a six-column line's approved field, so a false positive
        // silently mis-carves the row.
        => SourceHash.IsWireDigest(value).ShouldBe(expected);
}
