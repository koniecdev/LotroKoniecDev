using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.CLI.Domain.ProgramArgs;

public sealed class CommandArg : ValueObject
{
    public static readonly string[] ValidCommands = ["export", "patch"];
    public string Value { get; }
    
    public bool IsExport => Value == ValidCommands[0];
    public bool IsPatch => Value == ValidCommands[1];

    public static Result<CommandArg> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<CommandArg>(
                new Error("CommandArg.NullOrWhitespace", 
                    "value cannot be null or whitespace", ErrorType.Validation));
        }

        value = value.Trim().ToLowerInvariant();

        if (!ValidCommands.Contains(value))
        {
            return Result.Failure<CommandArg>(
                new Error("CommandArg.InvalidValue", 
                    $"value '{value}' is not a valid command", ErrorType.Validation));
        }

        CommandArg instance = new(value);
        return Result.Success(instance);
    }

    private CommandArg(string value)
    {
        Value = value;
    }

    public override string ToString() => Value;

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Value;
    }
}
