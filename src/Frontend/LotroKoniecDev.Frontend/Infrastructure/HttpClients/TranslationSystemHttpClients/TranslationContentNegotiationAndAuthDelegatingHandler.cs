using System.Net;
using System.Net.Http.Headers;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Authentication;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;

/// <summary>
/// Asks the TMS API for the link-carrying representation, by sending
/// <see cref="MediaTypes.HateoasJson"/> in <c>Accept</c>, and passes on the logged-in translator's
/// access token. The token is only added once an OIDC session exists; anonymous requests, such as the
/// public <c>GET /health</c> probe, go through without one.
/// A <c>401</c> on a logged-in call marks the session dead, so the next <c>OnValidatePrincipal</c> signs
/// it out cleanly. That is the fallback behind the signature check we do ourselves.
/// </summary>
internal sealed class TranslationContentNegotiationAndAuthDelegatingHandler : DelegatingHandler
{
    private const string BearerScheme = "Bearer";
    private const string AccessTokenName = "access_token";
    private const string SubjectClaimType = "sub";

    private static readonly MediaTypeWithQualityHeaderValue HateoasJsonMediaType = new(MediaTypes.HateoasJson);

    private readonly IHttpContextAccessor _httpContextAccessor;

    public TranslationContentNegotiationAndAuthDelegatingHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(HateoasJsonMediaType);

        HttpContext? httpContext = _httpContextAccessor.HttpContext;
        if (httpContext?.User.Identity?.IsAuthenticated is not true)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        string? accessToken = await httpContext.GetTokenAsync(AccessTokenName);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, accessToken);
        }

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        // The fallback path: the token was accepted here but refused by the API, usually in the window
        // where our copy of the signing keys is out of date. Mark the session dead, so the next
        // OnValidatePrincipal signs it out cleanly. We cannot call SignOutAsync here, because the SSR
        // response may already be streaming.
        // The signature check we do ourselves is the main path; this only covers the gap before the
        // frontend fetches the new keys.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await MarkSessionDeadAsync(httpContext, cancellationToken);
        }

        return response;
    }

    private static async Task MarkSessionDeadAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        string? subject = httpContext.User.FindFirst(SubjectClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        IDeadSessionRegistry registry = httpContext.RequestServices
            .GetRequiredService<IDeadSessionRegistry>();
        await registry.MarkDeadAsync(subject, cancellationToken);
    }
}
