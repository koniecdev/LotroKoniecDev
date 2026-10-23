namespace LotroKoniecDev.TranslationSystem.Primitives.Constants;

/// <summary>
/// DAT limits the TMS must respect even though it never opens a DAT. Text the DAT cannot hold is
/// refused here, where the translator can still be told. The patcher keeps its own copy in
/// <c>LotroKoniecDev.Primitives.Constants.DatFileConstants</c>: the contexts share data, not code.
/// </summary>
public static class DatFormatConstants
{
    /// <summary>
    /// The longest Polish text one row may hold, in UTF-16 code units (ADR-0043). The DAT stores each
    /// piece behind a two-byte length prefix, and the TMS does not know how the patcher cuts pieces, so
    /// it caps the whole text: stricter, never too loose. No row of the shipped corpus comes close.
    /// Escaping (ADR-0039) is undone before the DAT write, so this is the length the DAT sees.
    /// </summary>
    public const int MaxTranslatedTextLength = 32767;
}
