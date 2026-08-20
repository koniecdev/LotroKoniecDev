using System.Text;

namespace LotroKoniecDev.Application.Parsers;

/// <summary>
/// The content escape of the <c>||</c> translation file (ADR-0039): <c>\</c> becomes <c>\\</c>, CR
/// becomes <c>\r</c> and LF becomes <c>\n</c>. A fragment with real newlines then stays on one line,
/// and because the escape character is escaped too, no two different texts can produce the same
/// output. Every writer of the file escapes and every reader unescapes. Text in memory, in the DAT and
/// in the TMS database is always the raw form.
/// </summary>
/// <remarks>
/// The TMS has an identical copy in its own <c>Parsing</c> namespace, because the two bounded contexts
/// share the file and never the code (CLAUDE.md). The parity test suites keep the copies the same.
/// </remarks>
internal static class TranslationLineEscaper
{
    private const char EscapeCharacter = '\\';
    private const char CarriageReturnMarker = 'r';
    private const char LineFeedMarker = 'n';

    /// <summary>
    /// Turns raw content into the single-line form the file stores.
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
    /// Turns the file form back into raw content. A sequence no writer of ours can produce, such as
    /// <c>\t</c> or a single backslash at the end, is kept as it is instead of being rejected. Only a
    /// file written before ADR-0039 can hold one.
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
