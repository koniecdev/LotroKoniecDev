using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;

namespace LotroKoniecDev.Frontend.Components.Pages.Account;

/// <summary>
/// The GDPR export file (ADR-0032). When the TMS part could not be fetched, <see cref="TranslationData"/>
/// is <c>null</c> and <see cref="IsComplete"/> is <c>false</c>: a partial export is better than none.
/// </summary>
internal sealed record AccountDataExportFile(
    AuthDataExportDto AuthData,
    TranslatorDataExportResponse? TranslationData,
    bool IsComplete);
