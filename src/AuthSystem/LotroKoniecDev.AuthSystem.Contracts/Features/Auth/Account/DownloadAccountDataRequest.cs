namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;

/// <summary>
/// The step-up the GDPR export asks for (#690): the caller's current password, sent with the POST that
/// hands the export over. The account representation behind the matching GET needs no password, because
/// the account page renders it on every visit.
/// </summary>
public sealed record DownloadAccountDataRequest(string Password);
