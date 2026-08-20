using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// The result of reading a translation file: the rows that parsed, plus one warning per line that did
/// not. A rejected line is never dropped in silence (ADR-0042). The warnings go into
/// <c>PatchSummaryResponse.Warnings</c>, which the CLI prints after a patch run.
/// </summary>
/// <param name="Translations">The rows that parsed, sorted by file id and then gossip id.</param>
/// <param name="Warnings">
/// One entry per rejected line, up to a limit so a badly broken file cannot flood the console, and a
/// final "… and N more" entry once that limit is reached.
/// </param>
/// <param name="RejectedLineCount">
/// How many lines were rejected in total. Unlike <paramref name="Warnings"/> this is not limited, so
/// a caller can report the real scale of the problem.
/// </param>
public sealed record TranslationParseResult(
    IReadOnlyList<Translation> Translations,
    IReadOnlyList<string> Warnings,
    int RejectedLineCount);
