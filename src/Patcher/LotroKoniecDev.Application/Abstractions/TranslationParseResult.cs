using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// The outcome of reading a translation file: the rows that parsed, plus one warning per line that
/// did not. A rejected line is never silently dropped (ADR-0042) — the warnings travel up into
/// <c>PatchSummaryResponse.Warnings</c>, which the CLI prints after a patch run.
/// </summary>
/// <param name="Translations">The rows that parsed, sorted by file id then gossip id.</param>
/// <param name="Warnings">
/// One entry per rejected line, capped so a wholly corrupt file cannot flood the console, plus a
/// trailing "… and N more" entry once the cap is hit.
/// </param>
/// <param name="RejectedLineCount">
/// How many lines were actually rejected — uncapped, unlike <paramref name="Warnings"/>, so a caller
/// can report the true scale of the damage.
/// </param>
public sealed record TranslationParseResult(
    IReadOnlyList<Translation> Translations,
    IReadOnlyList<string> Warnings,
    int RejectedLineCount);
