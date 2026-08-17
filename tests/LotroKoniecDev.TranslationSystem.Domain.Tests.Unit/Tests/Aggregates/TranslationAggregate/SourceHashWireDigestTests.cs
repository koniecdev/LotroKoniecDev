using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslationAggregate;

/// <summary>
/// The TMS half of the <c>source_digest</c> contract (ADR-0047). Since the digest became a wire
/// value, this type's framing, truncation and byte order stopped being internal details.
/// </summary>
public sealed class SourceHashWireDigestTests
{
    [Theory]
    [MemberData(nameof(SourceDigestGoldenCases.All), MemberType = typeof(SourceDigestGoldenCases))]
    public void ToWireDigest_OnTheGoldenTriple_ShouldProduceTheContractDigest(
        string text, string? argsOrder, string? argsId, string expected)
        // The twin of this test lives in the patcher's Tests.Unit over the same fixture. Both must
        // agree with a value neither implementation produced — that is the only thing standing
        // between a one-sided framing change and every row of every artifact becoming unpatchable.
        => SourceHash.Compute(text, argsOrder, argsId).ToWireDigest().ShouldBe(expected);

    [Fact]
    public void ToWireDigest_ShouldEmitSixteenLowercaseHexCharacters()
        => SourceHash.Compute("Cokolwiek", null, null).ToWireDigest().ShouldMatch("^[0-9a-f]{16}$");

    [Fact]
    public void ToWireDigest_ShouldBeTheDigestBytesInDigestOrderNotTheUlongsHexRendering()
    {
        // Arrange — High is those same bytes read LITTLE-endian, so rendering the ulong as x16
        // reverses them. That mistake produces a perfectly well-formed 16-hex column which no
        // patcher would ever match, and nothing but this assertion would notice.
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
        // This is what tells a seventh column apart from a six-column line's approved field, so a
        // false positive here silently mis-carves the whole row.
        => SourceHash.IsWireDigest(value).ShouldBe(expected);
}
