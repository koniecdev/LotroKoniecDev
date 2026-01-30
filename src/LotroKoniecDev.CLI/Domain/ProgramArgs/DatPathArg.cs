using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.CLI.Domain.ProgramArgs;

public sealed class DatPathArg : ValueObject
{
    public const int MaxLength = 150;
    public string Value { get; }

    public static Result<DatPathArg> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<DatPathArg>(
                new Error("DatPathArg.NullOrWhitespace", 
                    "value cannot be null or whitespace", ErrorType.Validation));
        }

        value = value.Trim();

        if (value.Length > MaxLength)
        {
            return Result.Failure<DatPathArg>(
                new Error("DatPathArg.MaxLengthExceeded", 
                    $"value length cannot be greater than {MaxLength}", ErrorType.Validation));
        }

        DatPathArg instance = new(value);
        return Result.Success(instance);
    }

    private DatPathArg(string value)
    {
        Value = value;
    }

    public override string ToString() => Value;

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Value;
    }
}

