using System.Globalization;
using System.Text;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// Produces the LOTRO <c>||</c> translation file
/// (<c>file_id||gossip_id||content||args_order||args_id||approved</c>). Byte-compatible with the
/// patcher's own writer: content is emitted verbatim (already <c>\r</c>/<c>\n</c>-escaped on import),
/// absent args become <c>NULL</c>, the approved column is always <c>1</c>, and lines are CRLF-
/// terminated for a deterministic content hash across platforms. A golden fixture + a cross-parser
/// round-trip test guard the contract against drift from the patcher.
/// </summary>
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
                .Append(row.Content).Append(FieldSeparator)
                .Append(row.ArgsOrder ?? NullArgs).Append(FieldSeparator)
                .Append(row.ArgsId ?? NullArgs).Append(FieldSeparator)
                .Append('1').Append(LineTerminator);
        }

        return builder.ToString();
    }
}
