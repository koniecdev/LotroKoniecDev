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
/// <c>export-account-data</c> link in auth discovery, fetches the GDPR export, which is the account
/// resource whose <c>Links</c> decide what else the user may do, and follows that resource's
/// <c>delete-account</c> and <c>change-password</c> links.
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
    /// Loads the account export: auth discovery, then the <c>export-account-data</c> link, then a GET.
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

        return await _client.GetApiResultAsync<AccountDataExportResponse>(
            exportLink.Href,
            cancellationToken);
    }

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
