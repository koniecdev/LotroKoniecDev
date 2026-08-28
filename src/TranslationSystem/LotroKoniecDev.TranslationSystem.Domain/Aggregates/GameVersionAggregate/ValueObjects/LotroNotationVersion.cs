using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;

/// <summary>
/// The LOTRO game version in dotted-numeric form, as published in the official forum release notes
/// (for example <c>48.0</c> or <c>47.1.1</c>). This is the reliable content-version identifier.
/// </summary>
/// <remarks>
/// <para>
/// Input must look like <c>digits(.digits)*</c> and may carry at most three segments. Anything else
/// is rejected with <see cref="DomainErrors.GameVersionEntity.VersionProperty.InvalidFormat"/> or
/// <see cref="DomainErrors.GameVersionEntity.VersionProperty.MoreSegmentsThanAllowed"/>.
/// </para>
/// <para>
/// <see cref="Value"/> holds the canonical form: trailing zero segments are dropped, so <c>48</c>,
/// <c>48.0</c> and <c>48.0.0</c> are the same version. Zeros in the middle (<c>47.0.1</c>) matter and
/// are kept. See ADR-0003.
/// </para>
/// </remarks>
public sealed class LotroNotationVersion : ValueObject
{
    /// <summary>
    /// The longest input the domain accepts, which is three segments of three digits plus the two
    /// separators (<c>123.456.789</c>). See ADR-0003, amendment 2026-08-28.
    /// </summary>
    public const int VersionMaxLength = 11;

    /// <summary>
    /// The forum has never published a four-segment version, so a fourth segment can only be a typo.
    /// See ADR-0003, amendment 2026-08-28.
    /// </summary>
    public const int VersionMaxSegmentsCount = 3;

    private const char SegmentSeparator = '.';

    public string Value { get; }

    public static Result<LotroNotationVersion> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<LotroNotationVersion>(DomainErrors.GameVersionEntity.VersionProperty.NullOrEmpty);
        }

        value = value.Trim();

        // Both bounds are checked on the raw input on purpose. The forum publishes short two or three
        // segment versions, so a longer input is wrong whatever the canonical form would be: "47.1.0.0"
        // is refused even though it would collapse to "47.1".
        if (value.Count(SegmentSeparator) + 1 > VersionMaxSegmentsCount)
        {
            return Result.Failure<LotroNotationVersion>(
                DomainErrors.GameVersionEntity.VersionProperty.MoreSegmentsThanAllowed);
        }

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
    /// Returns the canonical string for dotted-numeric input, or <see cref="Maybe{T}.None"/> when the
    /// input is not <c>digits(.digits)*</c>. It works on the string only, so a very long segment can
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
    /// Strips leading zeros from a digit segment and keeps a single <c>0</c> for an all-zero segment
    /// (<c>047</c> becomes <c>47</c>, <c>000</c> becomes <c>0</c>).
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
