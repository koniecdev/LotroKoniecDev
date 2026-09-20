using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Mvc;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Components.Pages.Account;

/// <summary>
/// Makes the account pages' calls to the auth API through the typed client (LEGAL-02). It finds the
/// <c>export-account-data</c> link in auth discovery, fetches the account resource whose <c>Links</c>
/// decide what else the user may do, and follows that resource's links: <c>delete-account</c>,
/// <c>change-password</c> and <c>download-account-data</c>, the password-gated GDPR export (#690).
/// It stays a thin injectable class, so the pages' data flow can be unit-tested against a substituted
/// client and bUnit render tests can drive the pages through a substituted loader.
/// </summary>
internal sealed class AccountLoader
{
    private readonly IDiscoveryCache _discoveryCache;
    private readonly IAuthSystemClient _client;

    public AccountLoader(IDiscoveryCache discoveryCache, IAuthSystemClient client)
    {
        _discoveryCache = discoveryCache;
        _client = client;
    }

    /// <summary>
    /// Loads the account resource: auth discovery, then the <c>export-account-data</c> link, then a GET.
    /// When that link is missing in a logged-in session, the API does not offer the account section to
    /// this caller. That becomes a 403 <see cref="ProblemDetails"/>, and it is never decided here from
    /// role claims.
    /// </summary>
    public async Task<ApiResult<AccountDataExportResponse>> LoadExportAsync(
        CancellationToken cancellationToken = default)
    {
        ApiResult<AuthDiscoveryResponse> discoveryResult =
            await _discoveryCache.GetAuthSystemDiscoveryAsync(cancellationToken);
        if (discoveryResult.IsFailure)
        {
            return ApiResult.Failure<AccountDataExportResponse>(discoveryResult.ProblemDetails!);
        }

        LinkDto? exportLink = discoveryResult.Value.Links.FindLink(Rels.ExportAccountData);
        if (exportLink is null)
        {
            return ApiResult.Failure<AccountDataExportResponse>(ApiProblemCopy.FrontendAuthored(
                "Sekcja konta jest niedostępna",
                "Serwer nie udostępnia danych konta dla tej sesji. Zaloguj się ponownie.",
                StatusCodes.Status403Forbidden));
        }

        return RejectABodyWithoutAuthData(await _client.GetApiResultAsync<AccountDataExportResponse>(
            exportLink.Href,
            cancellationToken));
    }

    /// <summary>
    /// Asks the auth API to hand the export over, which it does only when the current password comes
    /// with the request (#690, ADR-0052).
    /// The target is the <c>download-account-data</c> link on the account resource, fetched fresh on
    /// every attempt and never read from the day-cached discovery document (ADR-0052 says why). It is
    /// the same document the account page gates its export button on, so the button and this call can
    /// never disagree.
    /// A rel we cannot find is a refusal, never a locally composed path (#610).
    /// </summary>
    public async Task<ApiResult<AccountDataExportResponse>> DownloadExportAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        ApiResult<AccountDataExportResponse> representation = await LoadExportAsync(cancellationToken);
        if (representation.IsFailure)
        {
            return representation;
        }

        LinkDto? downloadLink = representation.Value.Links.FindLink(Rels.DownloadAccountData);
        if (downloadLink is null)
        {
            return ApiResult.Failure<AccountDataExportResponse>(ApiProblemCopy.FrontendAuthored(
                "Pobieranie danych jest niedostępne",
                "Serwer nie udostępnia tej operacji dla tej sesji. Spróbuj ponownie za chwilę.",
                StatusCodes.Status403Forbidden));
        }

        return RejectABodyWithoutAuthData(await _client.PostApiResultAsync<AccountDataExportResponse>(
            downloadLink.Href,
            new DownloadAccountDataRequest(password),
            cancellationToken));
    }

    /// <summary>
    /// A 200 whose JSON parses but carries no <c>authData</c> — a proxy's own body, for one — is a bad
    /// gateway, not an account. The serializer fills a missing constructor argument with null, and both
    /// callers read <c>AuthData</c> straight away, so it is refused here: nothing may throw because of
    /// what came off the wire.
    /// </summary>
    private static ApiResult<AccountDataExportResponse> RejectABodyWithoutAuthData(
        ApiResult<AccountDataExportResponse> result) =>
        result.IsSuccess && result.Value.AuthData is null
            ? ApiResult.Failure<AccountDataExportResponse>(ApiProblemCopy.StatusOnly(StatusCodes.Status502BadGateway))
            : result;

    /// <summary>
    /// Schedules an account deletion, which happens in two phases (ADR-0031). On success the data comes
    /// back in response headers, <c>X-Deletion-Scheduled-At</c> and <c>X-Deletion-Finalizes-At</c>, and
    /// not in a body.
    /// </summary>
    public Task<ApiResult<ApiResponseHeaders>> ScheduleDeletionAsync(
        string href,
        string password,
        CancellationToken cancellationToken = default)
    {
        return _client.PostForHeadersApiResultAsync(
            href,
            new DeleteAccountRequest(password),
            cancellationToken);
    }

    /// <summary>
    /// Starts an e-mail change. Nothing on the account moves yet: the address changes only when the
    /// link sent to the new mailbox is used (ADR-0048).
    /// </summary>
    public Task<ApiResult> RequestEmailChangeAsync(
        string href,
        string newEmail,
        string currentPassword,
        CancellationToken cancellationToken = default)
    {
        return _client.PostApiResultAsync(
            href,
            new ChangeEmailRequest(newEmail, currentPassword),
            cancellationToken);
    }

    public Task<ApiResult> ChangePasswordAsync(
        string href,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        return _client.PostApiResultAsync(
            href,
            new ChangePasswordRequest(currentPassword, newPassword),
            cancellationToken);
    }
}
