using System.Net;
using System.Net.Http.Headers;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Authentication;

namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;

/// <summary>
/// Asks the TMS for the link-carrying representation and adds the access token when a session exists.
/// A <c>401</c> on a signed-in call marks the session dead: the API checks every token, and its refusal
/// ends the session (ADR-0067 decision 2).
/// </summary>
internal sealed class TranslationContentNegotiationAndAuthDelegatingHandler : DelegatingHandler
{
    private const string BearerScheme = "Bearer";
    private const string AccessTokenName = "access_token";

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

        // Not SignOutAsync here: the SSR response may already be streaming.
        if (response.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(accessToken))
        {
            await MarkSessionDeadAsync(httpContext, accessToken, cancellationToken);
        }

        return response;
    }

    /// <summary>Marks the token this call carried, not the user (#1061).</summary>
    private static async Task MarkSessionDeadAsync(
        HttpContext httpContext,
        string accessToken,
        CancellationToken cancellationToken)
    {
        IDeadSessionRegistry registry = httpContext.RequestServices
            .GetRequiredService<IDeadSessionRegistry>();
        await registry.MarkDeadAsync(accessToken, cancellationToken);
    }
}
