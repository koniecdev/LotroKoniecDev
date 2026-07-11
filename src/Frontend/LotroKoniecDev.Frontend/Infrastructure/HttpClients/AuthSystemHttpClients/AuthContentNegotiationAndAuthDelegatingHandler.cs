using System.Net;
using System.Net.Http.Headers;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Authentication;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;

/// <summary>
/// Negotiates the auth API's opt-in HATEOAS representation (sends
/// <see cref="MediaTypes.HateoasJson"/> in <c>Accept</c>) and forwards the signed-in translator's
/// bearer access token — the auth server validates its own tokens, so the same session bearer that
/// authorizes TMS calls authorizes the account endpoints. A <c>401</c> on an authenticated call
/// marks the session dead so the next <c>OnValidatePrincipal</c> signs it out cleanly (the reactive
/// backstop to the proactive JWKS check), mirroring the TMS handler.
/// </summary>
internal sealed class AuthContentNegotiationAndAuthDelegatingHandler : DelegatingHandler
{
    private const string BearerScheme = "Bearer";
    private const string AccessTokenName = "access_token";
    private const string SubjectClaimType = "sub";

    private static readonly MediaTypeWithQualityHeaderValue HateoasJsonMediaType = new(MediaTypes.HateoasJson);

    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuthContentNegotiationAndAuthDelegatingHandler(IHttpContextAccessor httpContextAccessor)
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
