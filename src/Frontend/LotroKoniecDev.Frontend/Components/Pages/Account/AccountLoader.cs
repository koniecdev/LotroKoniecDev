using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Mvc;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Components.Pages.Account;

/// <summary>
/// Drives the account pages' auth-API calls through the typed client (LEGAL-02): resolve the
/// <c>export-account-data</c> link from auth discovery, fetch the GDPR export envelope (the account
/// resource — its <c>Links</c> gate every further action), and follow the envelope's
/// <c>delete-account</c> / <c>change-password</c> rels. Kept as a thin injectable seam so the pages'
/// data flow is unit-testable over a substituted client and so bUnit render tests can drive the
/// pages through a substituted loader.
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
    /// Loads the account export envelope: auth discovery → <c>export-account-data</c> link → GET.
    /// A missing link under an authenticated session means the API does not serve the account
    /// section for this caller — surfaced as a 403 <see cref="ProblemDetails"/>, never invented
    /// locally from role claims.
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
            return ApiResult.Failure<AccountDataExportResponse>(new ProblemDetails
            {
                Title = "Sekcja konta jest niedostępna",
                Detail = "Serwer nie udostępnia danych konta dla tej sesji. Zaloguj się ponownie.",
                Status = StatusCodes.Status403Forbidden
            });
        }

        return await _client.GetApiResultAsync<AccountDataExportResponse>(
            exportLink.Href,
            cancellationToken);
    }

    /// <summary>
    /// Schedules account deletion (two-phase, ADR-0031). The success payload travels in response
    /// headers (<c>X-Deletion-Scheduled-At</c> / <c>X-Deletion-Finalizes-At</c>), not a body.
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
