namespace LotroKoniecDev.Primitives.Enums;

/// <summary>
/// Defines the types of errors that can occur in the application.
/// </summary>
public enum ErrorType
{
    /// <summary>
    /// Input validation error.
    /// </summary>
    Validation,

    /// <summary>
    /// Resource not found error.
    /// </summary>
    NotFound,

    /// <summary>
    /// Operation failure error.
    /// </summary>
    Failure,

    /// <summary>
    /// I/O operation error.
    /// </summary>
    IoError
}
