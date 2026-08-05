using System.Text;

namespace LotroKoniecDev.Application.Parsers;

/// <summary>
/// The content escape of the <c>||</c> translation file (ADR-0039): <c>\</c> becomes <c>\\</c>,
/// CR becomes <c>\r</c> and LF becomes <c>\n</c>, so a fragment carrying real newlines stays on one
/// line and the transform stays injective. Every writer of the file escapes and every reader
/// unescapes; text held in memory, in the DAT and in the TMS database is always the raw form.
/// </summary>
/// <remarks>
/// The TMS owns an identical copy in its own <c>Parsing</c> namespace — the two bounded contexts
/// share the file, never code (CLAUDE.md). The parity suites are what keep the copies honest.
/// </remarks>
internal static class TranslationLineEscaper
{
    private const char EscapeCharacter = '\\';
    private const char CarriageReturnMarker = 'r';
    private const char LineFeedMarker = 'n';

    /// <summary>
    /// Folds a raw content string into its single-line file representation.
    /// </summary>
    public static string Escape(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.AsSpan().IndexOfAny(EscapeCharacter, '\r', '\n') < 0)
        {
            return content;
        }

        StringBuilder builder = new(content.Length + 8);

        foreach (char character in content)
        {
            switch (character)
            {
                case EscapeCharacter:
                    builder.Append(EscapeCharacter).Append(EscapeCharacter);
                    break;
                case '\r':
                    builder.Append(EscapeCharacter).Append(CarriageReturnMarker);
                    break;
                case '\n':
                    builder.Append(EscapeCharacter).Append(LineFeedMarker);
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Unfolds a file representation back into raw content. A sequence no writer can produce
    /// (<c>\t</c>, a trailing lone backslash) is kept verbatim rather than rejected — only a file
    /// written before ADR-0039 can contain one.
    /// </summary>
    public static string Unescape(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!content.Contains(EscapeCharacter))
        {
            return content;
        }

        StringBuilder builder = new(content.Length);

        for (int index = 0; index < content.Length; index++)
        {
            char character = content[index];

            if (character != EscapeCharacter || index == content.Length - 1)
            {
                builder.Append(character);
                continue;
            }

            switch (content[index + 1])
            {
                case EscapeCharacter:
                    builder.Append(EscapeCharacter);
                    index++;
                    break;
                case CarriageReturnMarker:
                    builder.Append('\r');
                    index++;
                    break;
                case LineFeedMarker:
                    builder.Append('\n');
                    index++;
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}
