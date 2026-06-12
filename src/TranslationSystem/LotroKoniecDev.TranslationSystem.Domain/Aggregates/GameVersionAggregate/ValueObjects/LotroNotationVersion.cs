using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;

/// <summary>
/// The LOTRO game version in dotted notation as published in the official forum release notes
/// (e.g. <c>48.0</c>, <c>47.1.1</c>) — the reliable content-version identifier.
/// </summary>
public sealed class LotroNotationVersion : ValueObject
{
    public const int VersionMaxLength = 12;

    public string Value { get; }

    public static Result<LotroNotationVersion> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<LotroNotationVersion>(DomainErrors.GameVersionEntity.VersionProperty.NullOrEmpty);
        }

        value = value.Trim();
        if (value.Length > VersionMaxLength)
        {
            return Result.Failure<LotroNotationVersion>(DomainErrors.GameVersionEntity.VersionProperty.LongerThanAllowed);
        }

        LotroNotationVersion instance = new(value);

        return Result.Success(instance);
    }

    private LotroNotationVersion(string value)
    {
        Value = value;
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Value;
    }
}
