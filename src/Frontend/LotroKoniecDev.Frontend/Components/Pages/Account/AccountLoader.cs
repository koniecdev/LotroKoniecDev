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
/// The account pages' calls to the auth API (LEGAL-02). The account resource's links decide what else the
/// user may do. A thin class, so bUnit tests can drive the pages through a substituted loader.
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
    /// A missing <c>export-account-data</c> link means the API does not offer the account section to this
    /// caller, which becomes a 403. It is never decided here from role claims.
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
    /// The auth API hands the export over only with the current password (ADR-0052). The link comes from
    /// the account resource, fetched fresh on every attempt and never from the day-cached discovery
    /// document, so the export button and this call cannot disagree. A missing rel is a refusal (#610).
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
    /// A 200 whose JSON parses but has no <c>authData</c>, such as a proxy's own body, is a bad gateway. The
    /// serializer fills a missing constructor argument with null, and both callers read <c>AuthData</c>
    /// straight away.
    /// </summary>
    private static ApiResult<AccountDataExportResponse> RejectABodyWithoutAuthData(
        ApiResult<AccountDataExportResponse> result) =>
        result.IsSuccess && result.Value.AuthData is null
            ? ApiResult.Failure<AccountDataExportResponse>(ApiProblemCopy.StatusOnly(StatusCodes.Status502BadGateway))
            : result;

    /// <summary>
    /// The first of the two deletion phases (ADR-0031). The dates come back in response headers, not in a
    /// body.
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
    /// The address changes only when the link sent to the new mailbox is used (ADR-0048).
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
