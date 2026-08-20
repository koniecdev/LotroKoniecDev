namespace LotroKoniecDev.TranslationSystem.Primitives.Constants;

/// <summary>
/// Facts about the game's DAT format that the TMS has to respect even though it never opens a DAT.
/// The TMS writes the <c>||</c> file the patcher reads, so text the DAT cannot hold must be refused
/// here, where the translator can still be told about it.
/// The patcher keeps its own copy in <c>LotroKoniecDev.Primitives.Constants.DatFileConstants</c>: the
/// two contexts share a data contract, not code.
/// </summary>
public static class DatFormatConstants
{
    /// <summary>
    /// The longest Polish text one row may hold, in UTF-16 code units.
    /// The patcher cuts that text at its <c>&lt;--DO_NOT_TOUCH!--&gt;</c> markers and writes each piece
    /// into the DAT behind a two-byte length prefix, which cannot count higher than 32767. We cap the
    /// whole text instead of each piece, because the TMS does not know how the patcher cuts pieces.
    /// That is stricter than the DAT needs, but never too loose: a text inside this limit can never
    /// produce a piece that is too long.
    /// On the shipped corpus of 792,500 rows the longest English source is 5,959 characters and the
    /// average is 66, and no row is over the cap, so the extra strictness costs nothing.
    /// Escaping (ADR-0039) happens after this check and is undone before the DAT write, so the length
    /// measured here is the length the DAT sees.
    /// </summary>
    public const int MaxTranslatedTextLength = 32767;
}
