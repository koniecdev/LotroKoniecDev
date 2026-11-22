namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// How the list's search term must match the English or the Polish text (#1477). No mode cares about
/// case, and every character of the term matches itself.
/// </summary>
public enum TranslationSearchMatch
{
    /// <summary>The term may stand anywhere in the text. The default.</summary>
    Contains,

    /// <summary>No letter, digit or <c>_</c> stands right before or right after the term.</summary>
    WholeWord,

    /// <summary>
    /// The whole text is the term, ignoring white space at both edges and one grammar mark at the end
    /// (<c>[</c>, 1 to 4 ASCII letters, <c>]</c>), such as <c>[E]</c>.
    /// </summary>
    WholeText
}
