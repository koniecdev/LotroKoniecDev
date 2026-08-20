using System.Text.RegularExpressions;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;

/// <summary>
/// A translator's email, taken from the authenticated <c>email</c> claim. The claim can be missing,
/// so <see cref="Entities.Translator"/> keeps it optional and callers build this type only when there
/// is a value. The format check mirrors KittySaver's <c>Email</c> value object.
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
