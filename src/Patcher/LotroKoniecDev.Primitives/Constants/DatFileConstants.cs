namespace LotroKoniecDev.Primitives.Constants;

/// <summary>
/// Constants of the LOTRO DAT file format.
/// </summary>
public static class DatFileConstants
{
    /// <summary>A text file in the DAT archive has 0x25 as the high byte of its file id.</summary>
    public const int TextFileMarker = 0x25;

    /// <summary>
    /// Sits between text pieces in a translation file and marks where the game inserts its own
    /// variables.
    /// </summary>
    public const string PieceSeparator = "<--DO_NOT_TOUCH!-->";

    /// <summary>
    /// The largest value the DAT's variable-length integer can hold. Its two-byte form spends one bit
    /// on the continuation flag and keeps 15 for the number.
    /// </summary>
    public const int MaxVarLenValue = 0x7FFF;

    /// <summary>
    /// The longest single text piece the DAT can hold, in UTF-16 code units. A piece carries a length
    /// prefix in the encoding of <see cref="MaxVarLenValue"/>, so it has exactly that limit. A prefix
    /// that had to be cut short would throw off every following fragment in the subfile, so a piece
    /// that is too long can only be refused.
    /// </summary>
    public const int MaxTextPieceLength = MaxVarLenValue;
}
