using LotroKoniecDev.Application.Parsers;
using LotroKoniecDev.TranslationSystem.API.Parsing;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Parsing;

/// <summary>
/// The export half of the drift guard (<c>ParserContractParityTests</c> covers import): the TMS
/// serializer's output must parse through the patcher's <see cref="TranslationFileParser"/> with every
/// contract field intact.
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

        // Assert: no escapes here, so content compares directly.
        lines.Length.ShouldBe(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            LotroKoniecDev.Domain.Models.Translation parsed = patcher.ParseLine(lines[i]).Value;
            parsed.FileId.ShouldBe(rows[i].FileId);
            ((long)parsed.GossipId).ShouldBe(rows[i].GossipId);
            parsed.Content.ShouldBe(rows[i].Content);
            parsed.IsApproved.ShouldBeTrue();
            // Without it the file patches nothing (ADR-0047), even on a row whose content holds "||".
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
        // Arrange: ADR-0039 end to end: raw in the TMS, escaped in the file, raw again in the patcher
        // (#596).
        string serialized = new TranslationFileSerializer().Serialize([new ArtifactRow(1, 2, content, null, null, Digest)]);

        // Act
        string[] lines = serialized.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        LotroKoniecDev.Domain.Models.Translation parsed =
            new TranslationFileParser().ParseLine(lines.ShouldHaveSingleItem()).Value;

        // Assert
        parsed.Content.ShouldBe(content);
    }
}
