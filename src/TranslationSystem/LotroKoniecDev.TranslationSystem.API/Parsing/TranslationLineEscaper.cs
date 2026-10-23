using System.Text;

namespace LotroKoniecDev.TranslationSystem.API.Parsing;

/// <summary>
/// The content escape of the <c>||</c> file (ADR-0039): <c>\</c> to <c>\\</c>, CR to <c>\r</c>, LF to
/// <c>\n</c>. Escaping the escape character too keeps it injective. Only the file holds the escaped
/// form; the database holds the raw text.
/// </summary>
/// <remarks>
/// The patcher has a copy in its own <c>Parsers</c> namespace: the contexts share the file, never the
/// code. Change both in the same commit; the parity tests keep them equal.
/// </remarks>
internal static class TranslationLineEscaper
{
    private const char EscapeCharacter = '\\';
    private const char CarriageReturnMarker = 'r';
    private const char LineFeedMarker = 'n';

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
    /// A sequence no writer of ours produces, such as <c>\t</c> or a trailing backslash, is kept as it
    /// is: only a file written before ADR-0039 can hold one.
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
