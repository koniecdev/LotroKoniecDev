using System.Globalization;
using System.Text;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// Produces the LOTRO <c>||</c> translation file
/// (<c>file_id||gossip_id||content||args_order||args_id||approved||source_digest</c>).
/// Byte-compatible with the patcher's own writer: content is escaped through
/// <see cref="TranslationLineEscaper"/> (ADR-0039) so a translation carrying real newlines stays on
/// one line, absent args become <c>NULL</c>, the approved column is always <c>1</c>, and lines are
/// CRLF-terminated for a deterministic content hash across platforms. A golden fixture + a
/// cross-parser round-trip test guard the contract against drift from the patcher.
/// </summary>
/// <remarks>
/// The trailing <c>source_digest</c> (ADR-0047) is what makes the artifact patchable at all: the
/// patcher writes a row only when the fragment still holds the English that digest describes. It is
/// always emitted — the projector computes it for every row — so this writer has no six-column mode;
/// six-column files exist only as older artifacts and hand-made ones, which the READERS still accept.
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
