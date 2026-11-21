using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using LotroKoniecDev.AuthSystem.Contracts.Features.Admin;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Components.Pages.AccountAdministration;

/// <summary>
/// The admin's account page's calls to the auth API (#1488). The <c>accounts</c> rel is both the
/// permission and the URL, and each account's links say which role change it offers. A thin class, so a
/// bUnit test can drive the page through a substituted loader.
/// </summary>
internal sealed class AccountAdministrationLoader
{
    private const string PageParameter = "page";

    private readonly IDiscoveryCache _discoveryCache;
    private readonly IAuthSystemClient _client;

    public AccountAdministrationLoader(IDiscoveryCache discoveryCache, IAuthSystemClient client)
    {
        _discoveryCache = discoveryCache;
        _client = client;
    }

    /// <summary>
    /// A missing <c>accounts</c> rel means the server does not offer the list to this caller, which becomes
    /// a 403. It is never decided here from role claims.
    /// </summary>
    public async Task<ApiResult<AccountListResponse>> ListAccountsAsync(
        int page,
        CancellationToken cancellationToken = default)
    {
        ApiResult<AuthDiscoveryResponse> discoveryResult =
            await _discoveryCache.GetAuthSystemDiscoveryAsync(cancellationToken);
        if (discoveryResult.IsFailure)
        {
            return ApiResult.Failure<AccountListResponse>(discoveryResult.ProblemDetails!);
        }

        LinkDto? accountsLink = discoveryResult.Value.Links.FindLink(Rels.Accounts);
        if (accountsLink is null)
        {
            return ApiResult.Failure<AccountListResponse>(ApiProblemCopy.FrontendAuthored(
                "Lista kont jest niedostępna",
                "Serwer udostępnia listę kont tylko administratorowi.",
                StatusCodes.Status403Forbidden));
        }

        string href = page > 1
            ? QueryHelpers.AddQueryString(accountsLink.Href, PageParameter, page.ToString(CultureInfo.InvariantCulture))
            : accountsLink.Href;

        return await _client.GetApiResultAsync<AccountListResponse>(href, cancellationToken);
    }

    public Task<ApiResult> GrantReviewerAsync(string grantHref, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantHref);

        return _client.PutApiResultAsync(grantHref, cancellationToken);
    }

    public Task<ApiResult> RemoveReviewerAsync(string removeHref, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(removeHref);

        return _client.DeleteApiResultAsync(removeHref, cancellationToken);
    }
}
