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
    [Fact]
    public void SerializedRows_ShouldParseBackThroughThePatcherWithIdenticalFields()
    {
        // Arrange — includes a row whose content contains the || separator and args columns.
        ArtifactRow[] rows =
        [
            new(620756992, 1001, "Witaj w Srodziemiu!", null, null),
            new(620756992, 1004, "Multi||arg||content", "1-2", "1-2"),
            new(620756992, 1005, "Trailing approved", null, null),
        ];

        // Act
        string serialized = new TranslationFileSerializer().Serialize(rows);
        string[] lines = serialized.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        TranslationFileParser patcher = new();

        // Assert — every serialized line parses, and the contract fields match (no escapes here, so
        // the patcher's unescape is a no-op and content compares directly).
        lines.Length.ShouldBe(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            LotroKoniecDev.Domain.Models.Translation parsed = patcher.ParseLine(lines[i]).Value;
            parsed.FileId.ShouldBe(rows[i].FileId);
            ((long)parsed.GossipId).ShouldBe(rows[i].GossipId);
            parsed.Content.ShouldBe(rows[i].Content);
            parsed.IsApproved.ShouldBeTrue();
        }
    }

    [Fact]
    public void SerializedRow_WithEscapedNewline_ShouldBeUnescapedByThePatcher()
    {
        // Arrange — content is stored escaped; the patcher unescapes on its way into the DAT.
        string serialized = new TranslationFileSerializer().Serialize([new ArtifactRow(1, 2, @"Line1\nLine2", null, null)]);

        // Act
        LotroKoniecDev.Domain.Models.Translation parsed =
            new TranslationFileParser().ParseLine(serialized.TrimEnd('\r', '\n')).Value;

        // Assert
        parsed.Content.ShouldBe("Line1\nLine2");
    }
}
