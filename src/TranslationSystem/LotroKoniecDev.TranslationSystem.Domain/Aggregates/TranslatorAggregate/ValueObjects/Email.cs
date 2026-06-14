using System.Text.RegularExpressions;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;

/// <summary>
/// A translator's email, sourced from the authenticated <c>email</c> claim. Optional on the
/// <see cref="Entities.Translator"/> (the claim may be absent), so callers create it only when a
/// value is present; the constrained-string format check mirrors KittySaver's <c>Email</c> VO.
/// </summary>
public sealed partial class Email : ValueObject
{
    public const int MaxLength = 250;

    private const string RegexPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";

    private static readonly Regex EmailRegex = MailRegex();

    public string Value { get; }

    public static Result<Email> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<Email>(DomainErrors.TranslatorEntity.EmailProperty.InvalidFormat);
        }

        value = value.Trim();

        if (value.Length > MaxLength)
        {
            return Result.Failure<Email>(DomainErrors.TranslatorEntity.EmailProperty.LongerThanAllowed);
        }

        if (!EmailRegex.IsMatch(value))
        {
            return Result.Failure<Email>(DomainErrors.TranslatorEntity.EmailProperty.InvalidFormat);
        }

        Email instance = new(value);

        return Result.Success(instance);
    }

    private Email(string value)
    {
        Value = value;
    }

    public override string ToString() => Value;

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Value;
    }

    [GeneratedRegex(RegexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MailRegex();
}
