namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The Grafana rules on the box match these prefixes (ADR-0072). CD does not carry the rule file, so
/// a change here also needs the <c>/opt/obs</c> sync (runbook, Observability).
/// </summary>
internal static class TranslationSaveBudgetLogText
{
    public const string OverwrittenPrefix = "Translation overwritten:";
    public const string BudgetPrefix = "Translation save budget:";

    public const string OverwrittenTemplate =
        OverwrittenPrefix
        + " translator {TranslatorId} replaced the Polish of ({FileId}, {GossipId}), previous status {PreviousStatus}, budgeted {Budgeted}";

    /// <summary>
    /// Starts with neither prefix above, so neither Grafana rule counts a fill (ADR-0072 amendment of
    /// 2026-11-27).
    /// </summary>
    public const string FilledTemplate =
        "Identical rows filled: translator {TranslatorId} copied the Polish of ({FileId}, {GossipId}) into {FilledCount} empty rows with the same English, row saved {TranslationSaved}";

    /// <summary>
    /// A copy refused before it spent anything is the normal result of pressing the offered button, so it is
    /// Information and starts with neither prefix above (#977, ADR-0072 amendment of 2026-11-27).
    /// </summary>
    public const string FillRefusedTemplate =
        "Identical rows fill refused: translator {TranslatorId} needs {Needed} saves, {Saves} made in the last {WindowHours} h, budget {Budget}, limit {Limit}";

    public const string BudgetTemplate =
        BudgetPrefix
        + " translator {TranslatorId}, budget {Budget}, outcome {Outcome}, {Count} with this save, limit {Limit} per {WindowHours} h";
}
