using System.Globalization;
using System.Text;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// Writes the LOTRO <c>||</c> translation file
/// (<c>file_id||gossip_id||content||args_order||args_id||approved||source_digest</c>).
/// It produces the same bytes as the patcher's own writer: the content is escaped through
/// <see cref="TranslationLineEscaper"/> (ADR-0039), so a translation with real newlines stays on one
/// line, an absent args column becomes <c>NULL</c>, the approved column is always <c>1</c>, and lines
/// end with CRLF so the content hash is the same on every platform.
/// A golden fixture and a round-trip test through the other parser keep this in step with the patcher.
/// </summary>
/// <remarks>
/// The last column, <c>source_digest</c> (ADR-0047), is what makes the file patchable at all: the
/// patcher writes a row only when the fragment still holds the English that digest describes. It is
/// always written, because the projector computes it for every row, so this writer has no six-column
/// mode. Six-column files exist only as older artifacts and hand-made ones, which the readers still
/// accept.
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
