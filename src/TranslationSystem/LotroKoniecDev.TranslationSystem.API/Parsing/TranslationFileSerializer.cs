using System.Globalization;
using System.Text;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// Writes the <c>||</c> file (<c>file_id||gossip_id||content||args_order||args_id||approved||source_digest</c>)
/// byte for byte like the patcher's writer. Lines end with CRLF so the content hash is the same on
/// every platform. A golden fixture and a round trip through the other parser keep the two in step.
/// </summary>
/// <remarks>
/// No six-column mode: the patcher writes a row only when the fragment still holds the English that
/// <c>source_digest</c> describes (ADR-0047), so a file without it patches nothing.
/// </remarks>
internal sealed class TranslationFileSerializer : ITranslationFileSerializer
{
    private const string FieldSeparator = "||";
    private const string LineTerminator = "\r\n";
    private const string NullArgs = "NULL";

    public string Serialize(IReadOnlyList<ArtifactRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        StringBuilder builder = new();

        foreach (ArtifactRow row in rows)
        {
            builder
                .Append(row.FileId.ToString(CultureInfo.InvariantCulture)).Append(FieldSeparator)
                .Append(row.GossipId.ToString(CultureInfo.InvariantCulture)).Append(FieldSeparator)
                .Append(TranslationLineEscaper.Escape(row.Content)).Append(FieldSeparator)
                .Append(row.ArgsOrder ?? NullArgs).Append(FieldSeparator)
                .Append(row.ArgsId ?? NullArgs).Append(FieldSeparator)
                .Append('1').Append(FieldSeparator)
                .Append(row.SourceDigest).Append(LineTerminator);
        }

        return builder.ToString();
    }
}
