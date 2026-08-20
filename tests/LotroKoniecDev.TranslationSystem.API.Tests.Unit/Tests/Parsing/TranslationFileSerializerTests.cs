using LotroKoniecDev.TranslationSystem.API.Parsing;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

public sealed class TranslationFileSerializerTests
{
    /// <summary>
    /// A stand-in for the projector's <c>SourceHash.ToWireDigest()</c>. The serializer only copies the
    /// column through, so what it holds does not matter here. Making sure the value is the digest of the
    /// row's real English is the projector's job and is pinned by its own tests.
    /// </summary>
    private const string Digest = "3f9a1c0e7b2d4a55";

    private static string Serialize(params ArtifactRow[] rows) => new TranslationFileSerializer().Serialize(rows);

    [Fact]
    public void Serialize_WithNullArgs_ShouldEmitNullColumns()
        => Serialize(new ArtifactRow(620756992, 1001, "Witaj", null, null, Digest))
            .ShouldBe($"620756992||1001||Witaj||NULL||NULL||1||{Digest}\r\n");

    [Fact]
    public void Serialize_WithArgs_ShouldEmitThemVerbatim()
        => Serialize(new ArtifactRow(620756992, 1002, "Tekst", "1-2", "3-4", Digest))
            .ShouldBe($"620756992||1002||Tekst||1-2||3-4||1||{Digest}\r\n");

    [Fact]
    public void Serialize_ShouldAlwaysEmitApprovedOne()
        => Serialize(new ArtifactRow(1, 2, "x", null, null, Digest))
            .ShouldBe($"1||2||x||NULL||NULL||1||{Digest}\r\n");

    [Fact]
    public void Serialize_ShouldEmitTheSourceDigestAsTheLastColumn()
        => Serialize(new ArtifactRow(1, 2, "x", null, null, "00112233445566ff"))
            .ShouldBe("1||2||x||NULL||NULL||1||00112233445566ff\r\n");

    [Fact]
    public void Serialize_WithARealNewline_ShouldFoldItOntoOneLine()
        => Serialize(new ArtifactRow(1, 2, "Line1\nLine2", null, null, Digest))
            .ShouldBe(@"1||2||Line1\nLine2||NULL||NULL||1||" + Digest + "\r\n");

    [Fact]
    public void Serialize_WithARealCarriageReturnLineFeed_ShouldFoldBothCharacters()
        => Serialize(new ArtifactRow(1, 2, "Line1\r\nLine2", null, null, Digest))
            .ShouldBe(@"1||2||Line1\r\nLine2||NULL||NULL||1||" + Digest + "\r\n");

    [Fact]
    public void Serialize_WithALiteralBackslash_ShouldEscapeItSoTheParserCannotMistakeItForAnEscape()
        => Serialize(new ArtifactRow(1, 2, @"Line1\nLine2", null, null, Digest))
            .ShouldBe(@"1||2||Line1\\nLine2||NULL||NULL||1||" + Digest + "\r\n");

    [Fact]
    public void Serialize_WithSeparatorInContent_ShouldEmitItVerbatim()
        => Serialize(new ArtifactRow(1, 2, "Multi||arg||content", null, null, Digest))
            .ShouldBe($"1||2||Multi||arg||content||NULL||NULL||1||{Digest}\r\n");

    [Fact]
    public void Serialize_MultipleRows_ShouldUseCrLfPerLine()
        => Serialize(new ArtifactRow(1, 1, "a", null, null, Digest), new ArtifactRow(1, 2, "b", null, null, Digest))
            .ShouldBe($"1||1||a||NULL||NULL||1||{Digest}\r\n1||2||b||NULL||NULL||1||{Digest}\r\n");

    [Fact]
    public void Serialize_WithNoRows_ShouldReturnEmptyString()
        => Serialize().ShouldBe(string.Empty);
}
