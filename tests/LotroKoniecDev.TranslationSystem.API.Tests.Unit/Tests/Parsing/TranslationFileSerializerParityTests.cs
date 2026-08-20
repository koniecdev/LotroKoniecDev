using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.TranslationSystem.API.Parsing;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

/// <summary>
/// The export half of the inter-context drift guard (mirrors <c>ParserContractParityTests</c> on
/// the import side): the TMS serializer's output must parse byte-identically through the patcher's
/// own <see cref="TranslationFileParser"/>. Feeds serialized lines back into the patcher and asserts
/// the contract fields round-trip.
/// </summary>
public sealed class TranslationFileSerializerParityTests
{
    private const string Digest = "3f9a1c0e7b2d4a55";

    [Fact]
    public void SerializedRows_ShouldParseBackThroughThePatcherWithIdenticalFields()
    {
        // Arrange: includes a row whose content contains the || separator and args columns.
        ArtifactRow[] rows =
        [
            new(620756992, 1001, "Witaj w Srodziemiu!", null, null, "3f9a1c0e7b2d4a55"),
            new(620756992, 1004, "Multi||arg||content", "1-2", "1-2", "9c02e4d1a7f0b366"),
            new(620756992, 1005, "Trailing approved", null, null, "00112233445566ff"),
        ];

        // Act
        string serialized = new TranslationFileSerializer().Serialize(rows);
        string[] lines = serialized.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        TranslationFileParser patcher = new();

        // Assert: every serialized line parses, and the contract fields match (no escapes here, so
        // the patcher's unescape is a no-op and content compares directly).
        lines.Length.ShouldBe(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            LotroKoniecDev.Domain.Models.Translation parsed = patcher.ParseLine(lines[i]).Value;
            parsed.FileId.ShouldBe(rows[i].FileId);
            ((long)parsed.GossipId).ShouldBe(rows[i].GossipId);
            parsed.Content.ShouldBe(rows[i].Content);
            parsed.IsApproved.ShouldBeTrue();
            // The seventh column is what makes the file patchable at all (ADR-0047), so it has to
            // survive the writer and the reader unchanged, including on a row whose content holds "||".
            parsed.SourceDigest.ShouldBe(rows[i].SourceDigest);
        }
    }

    [Theory]
    [InlineData("Line1\nLine2")]
    [InlineData("Line1\r\nLine2")]
    [InlineData("Line1\rLine2")]
    [InlineData(@"C:\notes")]
    [InlineData(@"backslash \n here")]
    [InlineData(@"a||b" + "\n" + "c")]
    [InlineData("Zażółć gęślą jaźń")]
    public void SerializedRow_CarryingAnEscapableCharacter_ShouldReachThePatcherIntact(string content)
    {
        // Arrange: the end-to-end statement of ADR-0039: the TMS stores raw text, escapes it on the
        // way out, and the patcher unfolds it on the way into the DAT. This is #596's acceptance
        // criterion expressed across both contexts.
        string serialized = new TranslationFileSerializer().Serialize([new ArtifactRow(1, 2, content, null, null, Digest)]);

        // Act: the escape guarantees exactly one line, so trimming the terminator is safe.
        string[] lines = serialized.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        LotroKoniecDev.Domain.Models.Translation parsed =
            new TranslationFileParser().ParseLine(lines.ShouldHaveSingleItem()).Value;

        // Assert
        parsed.Content.ShouldBe(content);
    }
}
