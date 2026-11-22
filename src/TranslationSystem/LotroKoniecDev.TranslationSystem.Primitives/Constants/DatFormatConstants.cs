namespace LotroKoniecDev.TranslationSystem.Primitives.Constants;

/// <summary>
/// DAT limits the TMS must respect even though it never opens a DAT. Text the DAT cannot hold is
/// refused here, where the translator can still be told. The patcher keeps its own copy in
/// <c>LotroKoniecDev.Primitives.Constants.DatFileConstants</c>: the contexts share data, not code.
/// </summary>
public static class DatFormatConstants
{
    /// <summary>
    /// Marks where the game puts an argument (the <c>||</c> file contract). The patcher cuts the text on
    /// it, so a Polish text must hold as many as its English (#1437). The patcher's copy is
    /// <c>DatFileConstants.PieceSeparator</c>; a parity test keeps the two equal.
    /// </summary>
    public const string Placeholder = "<--DO_NOT_TOUCH!-->";

    /// <summary>
    /// The longest Polish text one row may hold, in UTF-16 code units (ADR-0043). The DAT stores each
    /// piece behind a two-byte length prefix, and the TMS does not know how the patcher cuts pieces, so
    /// it caps the whole text: stricter, never too loose. No row of the shipped corpus comes close.
    /// Escaping (ADR-0039) is undone before the DAT write, so this is the length the DAT sees.
    /// </summary>
    public const int MaxTranslatedTextLength = 32767;
}
