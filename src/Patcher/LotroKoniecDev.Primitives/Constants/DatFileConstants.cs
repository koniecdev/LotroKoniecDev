namespace LotroKoniecDev.Primitives.Constants;

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
    /// The longest text piece the DAT can hold, in UTF-16 code units: its length prefix uses the
    /// <see cref="MaxVarLenValue"/> encoding. A cut-short prefix would throw off every later fragment
    /// in the subfile, so a longer piece can only be refused (ADR-0043).
    /// </summary>
    public const int MaxTextPieceLength = MaxVarLenValue;
}
