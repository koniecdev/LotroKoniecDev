using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;

/// <summary>
/// The LOTRO game version in dotted-numeric notation as published in the official forum release
/// notes (e.g. <c>48.0</c>, <c>47.1.1</c>) — the reliable content-version identifier.
/// </summary>
/// <remarks>
/// <para>
/// Input must match the forum grammar <c>digits(.digits)*</c> (one or more non-empty
/// dot-separated runs of ASCII digits); anything else is rejected with
/// <see cref="DomainErrors.GameVersionEntity.VersionProperty.InvalidFormat"/>.
/// </para>
/// <para>
/// The stored <see cref="Value"/> is the <em>canonical</em> form: insignificant trailing-zero
/// segments are collapsed, so <c>48</c>, <c>48.0</c> and <c>48.0.0</c> are one and the same
/// version (equal <see cref="Value"/>, equal VOs). Interior zeros (<c>47.0.1</c>) are significant
/// and preserved. See ADR-0003.
/// </para>
/// </remarks>
public sealed class LotroNotationVersion : ValueObject
{
    public const int VersionMaxLength = 12;

    private const char SegmentSeparator = '.';

    public string Value { get; }

    public static Result<LotroNotationVersion> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<LotroNotationVersion>(DomainErrors.GameVersionEntity.VersionProperty.NullOrEmpty);
        }

        value = value.Trim();

        // Length is checked on the raw input deliberately: the forum producer emits short 2-3 segment
        // versions, so a >12-char input is malformed regardless of how trailing zeros would collapse.
        if (value.Length > VersionMaxLength)
        {
            return Result.Failure<LotroNotationVersion>(DomainErrors.GameVersionEntity.VersionProperty.LongerThanAllowed);
        }

        Maybe<string> canonical = Canonicalize(value);
        if (canonical.HasNoValue)
        {
            return Result.Failure<LotroNotationVersion>(DomainErrors.GameVersionEntity.VersionProperty.InvalidFormat);
        }

        LotroNotationVersion instance = new(canonical.Value);

        return Result.Success(instance);
    }

    private LotroNotationVersion(string value)
    {
        Value = value;
    }

    public override string ToString() => Value;

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Value;
    }

    /// <summary>
    /// Parses dotted-numeric input and returns its canonical string (each segment's leading zeros
    /// stripped, trailing all-zero segments dropped), or <see cref="Maybe{T}.None"/> when the input
    /// is not <c>digits(.digits)*</c>. Operates purely on the string so arbitrarily large segments
    /// never overflow a numeric type.
    /// </summary>
    private static Maybe<string> Canonicalize(string value)
    {
        string[] segments = value.Split(SegmentSeparator);

        string[] normalizedSegments = new string[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            if (!IsAsciiDigits(segments[i]))
            {
                return Maybe<string>.None;
            }

            normalizedSegments[i] = StripLeadingZeros(segments[i]);
        }

        int lastSignificant = normalizedSegments.Length - 1;
        while (lastSignificant > 0 && normalizedSegments[lastSignificant] == "0")
        {
            lastSignificant--;
        }

        return string.Join(SegmentSeparator, normalizedSegments.Take(lastSignificant + 1));
    }

    /// <summary>
    /// Strips leading zeros from an all-digit segment, leaving a single <c>0</c> for an all-zero
    /// segment (e.g. <c>047</c> → <c>47</c>, <c>000</c> → <c>0</c>).
    /// </summary>
    private static string StripLeadingZeros(string segment)
    {
        string trimmed = segment.TrimStart('0');

        return trimmed.Length is 0 ? "0" : trimmed;
    }

    private static bool IsAsciiDigits(string segment)
    {
        if (segment.Length is 0)
        {
            return false;
        }

        foreach (char character in segment)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
