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

    public const string BudgetTemplate =
        BudgetPrefix
        + " translator {TranslatorId}, budget {Budget}, outcome {Outcome}, {Count} with this save, limit {Limit} per {WindowHours} h";
}
