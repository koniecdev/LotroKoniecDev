using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

/// <summary>
/// The English source of a fragment as exported from the DAT. This is what the import diff compares.
/// The text and the two argument columns form one value, so equality covers all three: changing the
/// placeholder layout changes the meaning even when the text stays the same (spec 0001).
/// The text is the raw fragment text, with real newlines and real backslashes. The parser unescapes
/// what the file carries and the serializer escapes it again (ADR-0039), so the escape never reaches
/// this column. The args columns keep their file form (<c>1-2-3</c>, or <c>null</c> when absent);
/// they hold nothing that needs escaping.
/// </summary>
public sealed class TranslationSource : ValueObject
{
    public string Text { get; }
    public string? ArgsOrder { get; }
    public string? ArgsId { get; }

    public static Result<TranslationSource> Create(string text, string? argsOrder, string? argsId)
    {
        // The source text comes straight from the DAT, where an empty fragment is valid game content
        // and must survive a round trip. The parser never produces a null here, so a null is a
        // programmer error, not a row the import should reject.
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

    // A blank args column and the literal "NULL" both mean "no arguments". Both become null here, so
    // the diff never reads that difference as a source change.
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
