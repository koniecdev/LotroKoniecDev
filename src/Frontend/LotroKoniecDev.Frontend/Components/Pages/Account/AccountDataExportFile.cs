using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;

namespace LotroKoniecDev.Frontend.Components.Pages.Account;

/// <summary>
/// The finished GDPR export the download route serves (LEGAL-07, ADR-0032): the auth part plus the TMS
/// contribution part, both fetched with the caller's own token.
/// <see cref="IsComplete"/> is <c>false</c> when the TMS part could not be fetched. The export still
/// succeeds with <see cref="TranslationData"/> set to <c>null</c>, because a partial export is better
/// than none. If the auth part fails the download fails, because there is no export without the account
/// data.
/// </summary>
internal sealed record AccountDataExportFile(
    AuthDataExportDto AuthData,
    TranslatorDataExportResponse? TranslationData,
    bool IsComplete);
