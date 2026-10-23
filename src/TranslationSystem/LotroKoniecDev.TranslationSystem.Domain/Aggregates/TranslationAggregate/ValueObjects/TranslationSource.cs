using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

/// <summary>
/// The English source the import diff compares. Equality covers the text and both args columns,
/// because a new placeholder layout changes the meaning even when the text stays the same (spec 0001).
/// The text is raw, never escaped (ADR-0039); the args columns keep their file form (<c>1-2-3</c>).
/// </summary>
public sealed class TranslationSource : ValueObject
{
    public string Text { get; }
    public string? ArgsOrder { get; }
    public string? ArgsId { get; }

    public static Result<TranslationSource> Create(string text, string? argsOrder, string? argsId)
    {
        // An empty fragment is valid game content, so only null is refused, and the parser never
        // produces one: a programmer error, not a row to reject.
        ArgumentNullException.ThrowIfNull(text);

        TranslationSource instance = new(text, NormalizeArgs(argsOrder), NormalizeArgs(argsId));

        return Result.Success(instance);
    }

    private TranslationSource(string text, string? argsOrder, string? argsId)
    {
        Text = text;
        ArgsOrder = argsOrder;
        ArgsId = argsId;
    }

    // Blank and "NULL" both mean "no arguments", so the diff must not read them as a source change.
    private static string? NormalizeArgs(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return Text;
        yield return ArgsOrder ?? string.Empty;
        yield return ArgsId ?? string.Empty;
    }
}
