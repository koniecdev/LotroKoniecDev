using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;

namespace LotroKoniecDev.Frontend.Components.Pages.Account;

/// <summary>
/// The composed GDPR export document the download route serves (LEGAL-07, ADR-0032): the auth leg
/// plus the TMS contribution leg, fetched with the caller's own token. <see cref="IsComplete"/> is
/// <c>false</c> when the TMS leg could not be fetched — the export still succeeds with
/// <see cref="TranslationData"/> <c>null</c>, because a degraded export beats no export; the auth
/// leg failing fails the download instead (there is no export without the account data).
/// </summary>
internal sealed record AccountDataExportFile(
    AuthDataExportDto AuthData,
    TranslatorDataExportResponse? TranslationData,
    bool IsComplete);
