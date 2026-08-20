namespace LotroKoniecDev.Primitives.Enums;

/// <summary>
/// The kind of an <c>Error</c>. The CLI maps it to an exit code.
/// </summary>
public enum ErrorType
{
    Validation,
    NotFound,
    Failure,
    IoError
}
