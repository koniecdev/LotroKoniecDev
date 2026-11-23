using System.Globalization;
using System.Text.RegularExpressions;

namespace LotroKoniecDev.Frontend.Components.Pages.Editor;

/// <summary>
/// Compares the Polish text with the English text of the same row and lists the small slips that every
/// review sends back (#1489). It only warns: the save always goes through, because some differences are
/// right in Polish. The shapes of checks 1, 6 and 7 follow the owner decision of 2026-11-22 on #1489.
/// </summary>
internal static partial class TranslationSlipChecker
{
    /// <summary>The game writes a line break as these two characters, never as a real CR or LF (ADR-0039).</summary>
    private const string GameLineBreak = @"\n";

    /// <summary>The game's own quote sequence.</summary>
    private const string GameQuote = @"\q";

    private const string DoubleSpace = "  ";
    private const string ThreeDots = "...";
    private const char Ellipsis = '…';

    private static readonly char[] Blanks = [' ', '\t'];
    private static readonly char[] LineEnds = ['\r', '\n'];

    /// <summary>The slips in check order. A row with no Polish yet, or a blank one, has none.</summary>
    public static IReadOnlyList<TranslationSlip> Check(string sourceText, string? translatedText)
    {
        if (translatedText is null)
        {
            return [];
        }

        // The save removes these too (#1476), so they never reach the stored text.
        string polish = sourceText.EndsWith('\r') || sourceText.EndsWith('\n')
            ? translatedText
            : translatedText.TrimEnd(LineEnds);
        if (string.IsNullOrWhiteSpace(polish))
        {
            return [];
        }

        string english = sourceText;
        List<TranslationSlip> slips = [];

        bool englishStartsBlank = english.Length > 0 && IsBlank(english[0]);
        bool polishStartsBlank = IsBlank(polish[0]);
        bool englishEndsBlank = english.Length > 0 && IsBlank(english[^1]);
        bool polishEndsBlank = IsBlank(polish[^1]);

        if (polishStartsBlank != englishStartsBlank)
        {
            slips.Add(new TranslationSlip(
                TranslationSlipKind.BlankAtStart,
                polishStartsBlank
                    ? $"Na początku tłumaczenia jest {BlankName(polish[0])}, a w angielskim nie ma."
                    : $"Na początku tekstu angielskiego jest {BlankName(english[0])}, a w tłumaczeniu nie ma."));
        }

        if (polishEndsBlank != englishEndsBlank)
        {
            slips.Add(new TranslationSlip(
                TranslationSlipKind.BlankAtEnd,
                polishEndsBlank
                    ? $"Na końcu tłumaczenia jest {BlankName(polish[^1])}, a w angielskim nie ma."
                    : $"Na końcu tekstu angielskiego jest {BlankName(english[^1])}, a w tłumaczeniu nie ma."));
        }

        // An edge that check 1 already reports is left out, so one slip never gives two lines.
        int middleStart = polishStartsBlank && !englishStartsBlank ? polish.Length - polish.TrimStart(Blanks).Length : 0;
        int middleEnd = polishEndsBlank && !englishEndsBlank ? polish.TrimEnd(Blanks).Length : polish.Length;
        if (!english.Contains(DoubleSpace, StringComparison.Ordinal)
            && polish[middleStart..middleEnd].Contains(DoubleSpace, StringComparison.Ordinal))
        {
            slips.Add(new TranslationSlip(
                TranslationSlipKind.DoubleSpace,
                "W tłumaczeniu są co najmniej dwie spacje z rzędu, a w angielskim nie ma."));
        }

        if (polish.Contains('"'))
        {
            slips.Add(new TranslationSlip(
                TranslationSlipKind.StraightQuote,
                "W tekstach gry nie ma znaku \". Mowa postaci zostaje w apostrofach ', inne cytaty piszemy w „…”."));
        }

        int englishLineBreaks = CountGameLineBreaks(english);
        int polishLineBreaks = CountGameLineBreaks(polish);
        if (polishLineBreaks != englishLineBreaks)
        {
            slips.Add(new TranslationSlip(
                TranslationSlipKind.LineBreakCount,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $@"Inna liczba \n niż w angielskim ({polishLineBreaks} zamiast {englishLineBreaks}). Liczą się dwa wpisane znaki \n, nie Enter.")));
        }

        string? englishMark = TrailingMarkOf(english);
        string? polishMark = TrailingMarkOf(polish);
        if (!string.Equals(englishMark, polishMark, StringComparison.Ordinal))
        {
            slips.Add(new TranslationSlip(TranslationSlipKind.GrammarMark, GrammarMarkMessage(englishMark, polishMark)));
        }

        if (english.TrimStart(Blanks).StartsWith('\'') && !polish.TrimStart(Blanks).StartsWith('\''))
        {
            slips.Add(new TranslationSlip(
                TranslationSlipKind.OpeningApostrophe,
                "Tekst angielski zaczyna się apostrofem ', a tłumaczenie nie."));
        }

        string englishTail = TrimTail(english);
        string polishTail = TrimTail(polish);
        if (englishTail.EndsWith('\'') && !EndsWithClosingQuote(polishTail))
        {
            slips.Add(new TranslationSlip(
                TranslationSlipKind.ClosingQuote,
                @"Tekst angielski kończy się apostrofem ', a tłumaczenie nie kończy się ani ', ani ”, ani \q."));
        }

        string englishBody = WithoutClosingQuote(englishTail);
        string polishBody = WithoutClosingQuote(polishTail);
        char? englishSign = FinalSignOf(englishBody);
        char? polishSign = FinalSignOf(polishBody);
        if (polishSign != englishSign)
        {
            slips.Add(new TranslationSlip(
                TranslationSlipKind.FinalSign,
                $"Inny znak na końcu niż w angielskim: w angielskim {DescribeEnd(englishSign, englishBody)}, " +
                $"w tłumaczeniu {DescribeEnd(polishSign, polishBody)}."));
        }

        return slips;
    }

    /// <summary><c>[</c>, one to four ASCII letters, <c>]</c> at the very end, blanks around it allowed (#1477).</summary>
    [GeneratedRegex(@"[ \t]*(?<mark>\[[A-Za-z]{1,4}\])[ \t]*\z")]
    private static partial Regex TrailingMark();

    private static bool IsBlank(char character) => character is ' ' or '\t';

    private static string BlankName(char blank) => blank == '\t' ? "tabulator" : "spacja";

    private static int CountGameLineBreaks(string text)
    {
        int count = 0;
        int index = text.IndexOf(GameLineBreak, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(GameLineBreak, index + GameLineBreak.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string? TrailingMarkOf(string text)
    {
        Match match = TrailingMark().Match(text);
        return match.Success ? match.Groups["mark"].Value : null;
    }

    private static string GrammarMarkMessage(string? englishMark, string? polishMark) =>
        (englishMark, polishMark) switch
        {
            (not null, null) => $"Tekst angielski kończy się znacznikiem {englishMark}, a tłumaczenie nie.",
            (null, not null) => $"Tłumaczenie kończy się znacznikiem {polishMark}, a tekst angielski nie ma znacznika.",
            _ => $"Tłumaczenie kończy się znacznikiem {polishMark}, a tekst angielski znacznikiem {englishMark}."
        };

    /// <summary>
    /// The owner's order: blanks, the grammar mark with the blanks around it, blanks again, then a trailing
    /// run of <c>\n</c>. Blanks between those <c>\n</c> go with them.
    /// </summary>
    private static string TrimTail(string text)
    {
        string tail = text.TrimEnd(Blanks);

        Match mark = TrailingMark().Match(tail);
        if (mark.Success)
        {
            tail = tail[..mark.Index].TrimEnd(Blanks);
        }

        while (tail.EndsWith(GameLineBreak, StringComparison.Ordinal))
        {
            tail = tail[..^GameLineBreak.Length].TrimEnd(Blanks);
        }

        return tail;
    }

    private static bool EndsWithClosingQuote(string tail) =>
        tail.EndsWith('\'') || tail.EndsWith('”') || tail.EndsWith(GameQuote, StringComparison.Ordinal);

    private static string WithoutClosingQuote(string tail)
    {
        if (tail.EndsWith(GameQuote, StringComparison.Ordinal))
        {
            return tail[..^GameQuote.Length];
        }

        return tail.EndsWith('\'') || tail.EndsWith('”') ? tail[..^1] : tail;
    }

    /// <summary>A run of three or more dots and <c>…</c> are the same sign. Two dots read as one.</summary>
    private static char? FinalSignOf(string text)
    {
        if (text.EndsWith(Ellipsis) || text.EndsWith(ThreeDots, StringComparison.Ordinal))
        {
            return Ellipsis;
        }

        return text.Length > 0 && text[^1] is '.' or '!' or '?' or ':' ? text[^1] : null;
    }

    /// <summary>
    /// Names what the text really ends with, so the line stays true when it ends with a quote that the
    /// check does not strip, such as <c>’</c> or <c>"</c>.
    /// </summary>
    private static string DescribeEnd(char? sign, string text) =>
        sign switch
        {
            '.' => "kropka (.)",
            '!' => "wykrzyknik (!)",
            '?' => "znak zapytania (?)",
            ':' => "dwukropek (:)",
            Ellipsis => "wielokropek (…)",
            _ => DescribeNoSign(text)
        };

    private static string DescribeNoSign(string text)
    {
        if (text.Length == 0)
        {
            return "brak znaku";
        }

        char last = text[^1];
        return last switch
        {
            ' ' => "brak znaku (na końcu jest spacja)",
            '\t' => "brak znaku (na końcu jest tabulator)",
            _ when char.IsLetterOrDigit(last) || char.IsSurrogate(last) || char.IsControl(last) => "brak znaku",
            _ => $"brak znaku (na końcu jest {last})"
        };
    }
}

internal enum TranslationSlipKind
{
    BlankAtStart,
    BlankAtEnd,
    DoubleSpace,
    StraightQuote,
    LineBreakCount,
    GrammarMark,
    OpeningApostrophe,
    ClosingQuote,
    FinalSign
}

/// <param name="Message">One short Polish sentence that says what differs from the English.</param>
internal sealed record TranslationSlip(TranslationSlipKind Kind, string Message);
