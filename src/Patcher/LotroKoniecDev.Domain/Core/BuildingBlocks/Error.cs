using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Domain.Core.BuildingBlocks;

/// <summary>
/// Represents an application error with code, message, and type.
/// </summary>
public sealed class Error : ValueObject
{
    public string Code { get; }
    public string Message { get; }
    public ErrorType Type { get; }

    /// <summary>
    /// Represents no error (success state).
    /// </summary>
    public static readonly Error None = new(string.Empty, string.Empty);

    public Error(string code, string message, ErrorType type = ErrorType.Failure)
    {
        Code = code;
        Message = message;
        Type = type;
    }

    public override string ToString() => $"[{Type}] {Code}: {Message}";

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Code;
        yield return Message;
        yield return Type;
    }

    // Factory methods for common error types
    public static Error Validation(string code, string message) =>
        new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) =>
        new(code, message, ErrorType.NotFound);

    public static Error Failure(string code, string message) =>
        new(code, message, ErrorType.Failure);

    public static Error IoError(string code, string message) =>
        new(code, message, ErrorType.IoError);
}
