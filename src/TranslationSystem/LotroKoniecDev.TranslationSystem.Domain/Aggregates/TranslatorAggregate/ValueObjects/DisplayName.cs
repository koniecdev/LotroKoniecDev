using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;

/// <summary>
/// The name a translator is shown by in the UI, in "submitted by" and "approved by". It comes from
/// the authenticated <c>name</c> claim when the profile is created, and it cannot be empty.
/// </summary>
public sealed class DisplayName : ValueObject
{
    public const int MaxLength = 150;

    public string Value { get; }

    public static Result<DisplayName> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<DisplayName>(DomainErrors.TranslatorEntity.DisplayNameProperty.NullOrEmpty);
        }

        value = value.Trim();

        if (value.Length > MaxLength)
        {
            return Result.Failure<DisplayName>(DomainErrors.TranslatorEntity.DisplayNameProperty.LongerThanAllowed);
        }

        DisplayName instance = new(value);

        return Result.Success(instance);
    }

    private DisplayName(string value)
    {
        Value = value;
    }

    public override string ToString() => Value;

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Value;
    }
}
