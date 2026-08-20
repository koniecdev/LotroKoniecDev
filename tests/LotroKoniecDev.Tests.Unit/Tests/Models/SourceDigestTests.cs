using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.Tests.Unit.Tests.Models;

/// <summary>
/// The patcher's half of the <c>source_digest</c> contract (ADR-0047). The golden theory is the
/// cross-context parity guard; the rest pins the export-form composition the guard depends on.
/// </summary>
public sealed class SourceDigestTests
{
    [Theory]
    [MemberData(nameof(SourceDigestGoldenCases.All), MemberType = typeof(SourceDigestGoldenCases))]
    public void Compute_OnTheGoldenTriple_ShouldProduceTheContractDigest(
        string text, string? argsOrder, string? argsId, string expected)
        // The twin of this test lives in TranslationSystem.Domain.Tests.Unit over the same fixture.
        // Both must agree with a value neither implementation produced, or an artifact's digests
        // would be unrecognisable to every patcher that downloads it.
        => SourceDigest.Compute(text, argsOrder, argsId).ShouldBe(expected);

    [Fact]
    public void Compute_ShouldEmitSixteenLowercaseHexCharacters()
        => SourceDigest.Compute("Cokolwiek", null, null).ShouldMatch("^[0-9a-f]{16}$");

    [Fact]
    public void Compute_WithAnAbsentArgsColumn_ShouldDifferFromAnEmptyOne()
        // The framing exists for exactly this: the value-object form distinguishes "no arguments"
        // from "an empty string", and the two contexts must agree on which one they hashed.
        => SourceDigest.Compute("Tekst", null, null).ShouldNotBe(SourceDigest.Compute("Tekst", "", null));

    [Fact]
    public void ForFragment_ShouldComposeTheSameTripleTheExporterWrites()
    {
        // Arrange: the whole guard rests on this equivalence: what `export` would write for this
        // fragment, and what the guard computes from it, must be the same triple.
        Fragment fragment = new() { Pieces = ["Part1", "Part2"] };

        // Assert
        SourceDigest.ForFragment(fragment)
            .ShouldBe(SourceDigest.ForExportForm($"Part1{DatFileConstants.PieceSeparator}Part2", 0));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1")]
    [InlineData(3, "1-2-3")]
    public void ForExportForm_ShouldUseIdentityArgumentColumnsFromTheArgumentCount(int argumentCount, string? expectedArgs)
        // `export` writes the args columns from the fragment's own ArgRefs.Count, and NULL when it has
        // none. The guard has to work them out the same way, from the fragment, and never from the args
        // columns of a hand-made row.
        => SourceDigest.ForExportForm("Tekst", argumentCount)
            .ShouldBe(SourceDigest.Compute("Tekst", expectedArgs, expectedArgs));

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
    public void IsWireForm_ShouldRecogniseOnlySixteenHexCharacters(string? value, bool expected)
        // This is what tells a seventh column apart from a six-column line's approved field, so a
        // false positive here silently mis-carves the whole row.
        => SourceDigest.IsWireForm(value).ShouldBe(expected);

    [Theory]
    [InlineData("a37cc1683216cd32", "A37CC1683216CD32", true)]
    [InlineData("a37cc1683216cd32", "b37cc1683216cd32", false)]
    [InlineData(null, "a37cc1683216cd32", false)]
    [InlineData("a37cc1683216cd32", null, false)]
    [InlineData(null, null, false)]
    public void Matches_ShouldCompareCaseInsensitivelyAndNeverAdmitAnAbsentDigest(string? left, string? right, bool expected)
        // Two absent digests are not a match: "we do not know" must never admit a write.
        => SourceDigest.Matches(left, right).ShouldBe(expected);
}
