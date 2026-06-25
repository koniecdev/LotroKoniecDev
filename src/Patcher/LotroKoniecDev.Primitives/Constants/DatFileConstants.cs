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
}
