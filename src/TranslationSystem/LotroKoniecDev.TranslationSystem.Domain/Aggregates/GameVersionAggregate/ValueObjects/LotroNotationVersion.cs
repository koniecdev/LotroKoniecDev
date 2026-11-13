using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;

/// <summary>
/// The game version as the official forum release notes publish it (<c>48.0</c>, <c>47.1.1</c>): the
/// reliable content-version identifier.
/// </summary>
/// <remarks>
/// <see cref="Value"/> is canonical: trailing zero segments are dropped, so <c>48</c>, <c>48.0</c> and
/// <c>48.0.0</c> are the same version, while a middle zero (<c>47.0.1</c>) is kept (ADR-0003).
/// </remarks>
public sealed class LotroNotationVersion : ValueObject, IComparable<LotroNotationVersion>
{
    /// <summary>
    /// Three segments of three digits plus two separators (<c>123.456.789</c>; ADR-0003, amendment
    /// 2026-08-28).
    /// </summary>
    public const int VersionMaxLength = 11;

    /// <summary>
    /// The forum has never published a four-segment version, so a fourth segment is a typo (ADR-0003,
    /// amendment 2026-08-28).
    /// </summary>
    public const int VersionMaxSegmentsCount = 3;

    private const char SegmentSeparator = '.';

    public string Value { get; }

    /// <summary>
    /// Compares the version numbers segment by segment, so <c>48.10</c> is higher than <c>48.9</c>,
    /// and a missing segment counts as <c>0</c>. This is the number order only: the registry still
    /// orders versions by registration date (wiki "Wersja gry", ADR-0003 amendment 2026-11-13).
    /// </summary>
    public int CompareTo(LotroNotationVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        string[] segments = Value.Split(SegmentSeparator);
        string[] otherSegments = other.Value.Split(SegmentSeparator);
        int segmentsCount = Math.Max(segments.Length, otherSegments.Length);

        for (int i = 0; i < segmentsCount; i++)
        {
            int comparison = CompareSegments(SegmentAt(segments, i), SegmentAt(otherSegments, i));
            if (comparison is not 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    public static bool operator <(LotroNotationVersion? left, LotroNotationVersion? right) =>
        Comparer<LotroNotationVersion>.Default.Compare(left, right) is < 0;

    public static bool operator <=(LotroNotationVersion? left, LotroNotationVersion? right) =>
        Comparer<LotroNotationVersion>.Default.Compare(left, right) is <= 0;

    public static bool operator >(LotroNotationVersion? left, LotroNotationVersion? right) =>
        Comparer<LotroNotationVersion>.Default.Compare(left, right) is > 0;

    public static bool operator >=(LotroNotationVersion? left, LotroNotationVersion? right) =>
        Comparer<LotroNotationVersion>.Default.Compare(left, right) is >= 0;

    public override string ToString() => Value;

    public static Result<LotroNotationVersion> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<LotroNotationVersion>(DomainErrors.GameVersionEntity.VersionProperty.NullOrEmpty);
        }

        value = value.Trim();

        string[] segments = value.Split(SegmentSeparator);

        if (segments.Length > VersionMaxSegmentsCount)
        {
            return Result.Failure<LotroNotationVersion>(
                DomainErrors.GameVersionEntity.VersionProperty.MoreSegmentsThanAllowed);
        }

        if (value.Length > VersionMaxLength)
        {
            return Result.Failure<LotroNotationVersion>(DomainErrors.GameVersionEntity.VersionProperty.LongerThanAllowed);
        }

        Maybe<string> canonical = Canonicalize(segments);
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

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Value;
    }

    /// <summary>
    /// Works on strings only, so a very long segment can never overflow a numeric type.
    /// </summary>
    private static Maybe<string> Canonicalize(string[] segments)
    {
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
    /// <c>047</c> becomes <c>47</c>, and <c>000</c> becomes <c>0</c>.
    /// </summary>
    private static string StripLeadingZeros(string segment)
    {
        string trimmed = segment.TrimStart('0');

        return trimmed.Length is 0 ? "0" : trimmed;
    }

    private static string SegmentAt(string[] segments, int index) =>
        index < segments.Length ? segments[index] : "0";

    /// <summary>
    /// A canonical segment has no leading zeros, so the longer run of digits is the higher number.
    /// </summary>
    private static int CompareSegments(string segment, string otherSegment) =>
        segment.Length != otherSegment.Length
            ? segment.Length.CompareTo(otherSegment.Length)
            : string.CompareOrdinal(segment, otherSegment);

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
