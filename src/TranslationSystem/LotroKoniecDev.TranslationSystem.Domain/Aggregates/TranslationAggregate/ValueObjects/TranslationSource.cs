using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

/// <summary>
/// The English source of a fragment as exported from the DAT — the unit the import diff compares.
/// Text and the two argument columns are one value (spec 0001: a placeholder-structure change is
/// a meaning change even without a text change), so equality covers all three. Stored verbatim in
/// the exported representation (<c>\r</c>/<c>\n</c> kept escaped, args as the raw <c>NULL</c>/<c>1-2-3</c>
/// column) so the distributed file round-trips byte-for-byte through the patcher.
/// </summary>
public sealed class TranslationSource : ValueObject
{
    public string Text { get; }
    public string? ArgsOrder { get; }
    public string? ArgsId { get; }

    public static Result<TranslationSource> Create(string text, string? argsOrder, string? argsId)
    {
        // Source text is exported verbatim from the DAT — empty fragments are legal game content and
        // must round-trip. A null here is never produced by the parser, so it is a programmer error,
        // not a per-row validation failure.
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

    // A blank or "NULL" args column carries no arguments — collapse both to null so the diff
    // never treats an absent-vs-NULL difference as a source change.
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
