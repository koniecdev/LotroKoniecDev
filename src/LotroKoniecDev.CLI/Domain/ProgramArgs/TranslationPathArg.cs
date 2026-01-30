
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.CLI.Domain.ProgramArgs;

public sealed class TranslationPathArg : ValueObject
{
    public const int MaxLength = 150;
    public string Value { get; }

    public static Result<TranslationPathArg> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<TranslationPathArg>(
                new Error("TranslationPathArg.NullOrWhitespace", 
                    "value cannot be null or whitespace", ErrorType.Validation));
        }

        value = value.Trim();

        if (value.Length > MaxLength)
        {
            return Result.Failure<TranslationPathArg>(
                new Error("TranslationPathArg.MaxLengthExceeded", 
                    $"value length cannot be greater than {MaxLength}", ErrorType.Validation));
        }

        TranslationPathArg instance = new(value);
        return Result.Success(instance);
    }

    private TranslationPathArg(string value)
    {
        Value = value;
    }

    public override string ToString() => Value;

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Value;
    }
}

