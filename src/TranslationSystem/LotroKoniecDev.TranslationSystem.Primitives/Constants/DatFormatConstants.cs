namespace LotroKoniecDev.TranslationSystem.Primitives.Constants;

/// <summary>
/// Facts about the game's DAT binary format that the TMS must respect even though it never touches
/// the DAT: it produces the <c>||</c> file the patcher writes back from, so content the DAT cannot
/// hold must be refused here, where a translator can still be told about it.
/// The patcher keeps its own copy in <c>LotroKoniecDev.Primitives.Constants.DatFileConstants</c> —
/// the two contexts share a data contract, not code.
/// </summary>
public static class DatFormatConstants
{
    /// <summary>
    /// Maximum length, in UTF-16 code units, of the Polish text of one row.
    /// The patcher splits that text on its <c>&lt;--DO_NOT_TOUCH!--&gt;</c> markers and writes each
    /// piece into the DAT behind a two-byte variable-length prefix, which cannot express more than
    /// 32767. Capping the whole text — rather than the per-piece limit the DAT actually enforces —
    /// is the strictest bound expressible without teaching the TMS how the patcher cuts pieces, and
    /// it can never be wrong in the unsafe direction: a text within it cannot produce an over-long
    /// piece. Measured on the shipped corpus (792,500 rows): longest English source 5,959 characters,
    /// average 66, zero rows above the cap — so the extra strictness costs nothing in practice.
    /// Escaping (ADR-0039) happens after this point and is undone before the DAT write, so the raw
    /// length measured here is the length the DAT sees.
    /// </summary>
    public const int MaxTranslatedTextLength = 32767;
}
