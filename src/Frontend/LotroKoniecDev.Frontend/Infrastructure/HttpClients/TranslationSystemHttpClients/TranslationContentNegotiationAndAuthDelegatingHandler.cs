using System.Net;
using System.Net.Http.Headers;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Authentication;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;

/// <summary>
/// Negotiates the TMS API's opt-in HATEOAS representation (sends
/// <see cref="MediaTypes.HateoasJson"/> in <c>Accept</c>) and forwards the signed-in translator's
/// bearer access token. The token only flows once the OIDC session exists; anonymous requests (e.g.
/// the public <c>GET /health</c> probe) pass through unauthenticated. A <c>401</c> on an
/// authenticated call marks the session dead so the next <c>OnValidatePrincipal</c> signs it out
/// cleanly (the reactive backstop to the proactive JWKS check).
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

        // Reactive backstop: the bearer token authenticated locally but was rejected upstream
        // (typically the stale-JWKS window). Mark the session dead so the next OnValidatePrincipal
        // signs it out cleanly — we cannot SignOutAsync here because the SSR response may already be
        // streaming. The proactive JWKS check is the primary path; this only catches the gap before the
        // FE refetches the rotated keys.
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
