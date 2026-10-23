namespace LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;

/// <summary>
/// The GDPR export's step-up (#690): the current password, sent with the POST that hands the export over.
/// The account GET needs no password, because the account page renders it on every visit.
/// </summary>
public sealed record DownloadAccountDataRequest(string Password) : IPasswordConfirmationRequest;
