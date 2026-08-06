namespace LotroKoniecDev.Primitives.Constants;

/// <summary>
/// Constants related to LOTRO DAT file handling.
/// </summary>
public static class DatFileConstants
{
    /// <summary>
    /// Marker byte indicating a text file in the DAT archive.
    /// Text files have 0x25 as the high byte of their file ID.
    /// </summary>
    public const int TextFileMarker = 0x25;

    /// <summary>
    /// Separator used between text pieces in translation files.
    /// This marker indicates positions where game variables are inserted.
    /// </summary>
    public const string PieceSeparator = "<--DO_NOT_TOUCH!-->";

    /// <summary>
    /// Largest value the DAT's variable-length integer encoding can express: its two-byte form
    /// spends one bit on the continuation flag, leaving 15.
    /// </summary>
    public const int MaxVarLenValue = 0x7FFF;

    /// <summary>
    /// Maximum length, in UTF-16 code units, of a single text piece written into the DAT.
    /// A piece is length-prefixed with <see cref="MaxVarLenValue"/>'s encoding, so it inherits that
    /// ceiling exactly — and a truncated prefix would desynchronise every following fragment in the
    /// subfile, so an over-long piece can only ever be refused.
    /// </summary>
    public const int MaxTextPieceLength = MaxVarLenValue;
}
