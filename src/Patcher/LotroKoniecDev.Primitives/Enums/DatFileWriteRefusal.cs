namespace LotroKoniecDev.Primitives.Enums;

/// <summary>
/// Why the game file itself refuses a write, as a read-write open of it reports (#1248).
/// </summary>
public enum DatFileWriteRefusal
{
    None = 0,
    ReadOnly,
    AccessDenied,
    InUse,
    NotFound,
    Other
}
